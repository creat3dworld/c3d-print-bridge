<?php
/**
 * C3D Shipping Dashboard — LAN bridge endpoints + bridge key settings.
 *
 * =====================================================================
 * DROP-IN FOR A LATER PLUGIN UPDATE. NOT PART OF PLUGIN v0.2.0.
 * Do NOT install this file on its own and do NOT copy it into the
 * v0.2.0 build. It is included with the bridge repo so the plugin
 * update that enables the bridge has a reviewed starting point.
 * It requires the main C3D Shipping Dashboard plugin (WooCommerce)
 * to be active. Protocol contract: bridge/docs/PROTOCOL.md.
 * =====================================================================
 *
 * What this file adds, when the plugin update wires it in:
 *  1. REST endpoints under /wp-json/c3d-shipping/v1/bridge/* that
 *     ONLY accept requests carrying a valid per-bridge API key in
 *     an "Authorization: Bearer <key>" header. No key = 401, always.
 *  2. A "Bridge key" admin page under the C3D Shipping menu where
 *     the owner generates a key (shown ONCE), rotates it (the old
 *     key stops working immediately), or revokes it (all bridge
 *     traffic stops being accepted).
 *
 * Key storage: only a hash is kept (HMAC-SHA256 with the site salt,
 * option "c3d_bridge_key_hash"). The plaintext is held in option
 * "c3d_bridge_key_once" only until it is displayed once, then it is
 * deleted. If the owner loses the key, generate a new one — the old
 * one cannot be recovered, by design.
 *
 * OPEN ITEM (dashboard side, not bridge side): the jobs endpoint
 * returns "file_url" from the job itself when present, else from an
 * owner-maintained "c3d_print_file_map" option (product/item name =>
 * URL). Until the dashboard maps products to their print files,
 * file_url is empty for most products and the bridge correctly
 * refuses those jobs with "No print file supplied". The bridge never
 * slices or invents a file.
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

/**
 * Verify the bridge API key from the Authorization header.
 * Returns true only when a key is configured AND the presented key
 * hashes to the stored hash (constant-time comparison).
 */
function c3d_bridge_check_key( $request ) {
    $stored = get_option( 'c3d_bridge_key_hash', '' );
    if ( '' === $stored ) {
        return new WP_Error( 'c3d_bridge_no_key', 'No bridge API key is configured on this site.', array( 'status' => 401 ) );
    }
    $header = $request->get_header( 'authorization' );
    if ( ! $header || 0 !== stripos( $header, 'Bearer ' ) ) {
        return new WP_Error( 'c3d_bridge_unauthorized', 'A valid bridge API key is required.', array( 'status' => 401 ) );
    }
    $presented = trim( substr( $header, 7 ) );
    if ( '' === $presented ) {
        return new WP_Error( 'c3d_bridge_unauthorized', 'A valid bridge API key is required.', array( 'status' => 401 ) );
    }
    $hash = hash_hmac( 'sha256', $presented, wp_salt() );
    if ( ! hash_equals( (string) $stored, $hash ) ) {
        return new WP_Error( 'c3d_bridge_unauthorized', 'Bridge API key rejected (revoked or rotated).', array( 'status' => 401 ) );
    }
    return true;
}

function c3d_bridge_ok() {
    return rest_ensure_response( array( 'ok' => true ) );
}

/**
 * POST /bridge/heartbeat — refresh the bridge heartbeat timestamp.
 * The dashboard treats a heartbeat older than 10 minutes as stale.
 */
function c3d_bridge_heartbeat( $request ) {
    update_option( 'c3d_lan_bridge_heartbeat', current_time( 'mysql' ) );
    return c3d_bridge_ok();
}

/**
 * POST /bridge/status — store the bridge's latest printer report
 * for the shop status board. Nothing is shown as running without
 * a bridge report, so this stores exactly what was reported.
 */
function c3d_bridge_status( $request ) {
    $body = $request->get_json_params();
    if ( ! is_array( $body ) ) {
        $body = array();
    }
    $printers = array();
    if ( isset( $body['printers'] ) && is_array( $body['printers'] ) ) {
        foreach ( $body['printers'] as $p ) {
            if ( ! is_array( $p ) || empty( $p['name'] ) ) {
                continue;
            }
            $ams = isset( $p['ams'] ) && is_array( $p['ams'] ) ? $p['ams'] : array();
            $printers[] = array(
                'name'             => sanitize_text_field( $p['name'] ),
                'state'            => sanitize_text_field( isset( $p['state'] ) ? $p['state'] : 'offline' ),
                'progress_percent' => isset( $p['progress_percent'] ) && is_numeric( $p['progress_percent'] ) ? floatval( $p['progress_percent'] ) : null,
                'remaining_seconds' => isset( $p['remaining_seconds'] ) && is_numeric( $p['remaining_seconds'] ) ? intval( $p['remaining_seconds'] ) : null,
                'current_job_key'  => isset( $p['current_job_key'] ) ? sanitize_text_field( $p['current_job_key'] ) : '',
                'detail'           => isset( $p['detail'] ) ? sanitize_text_field( $p['detail'] ) : '',
                'ams'              => array(
                    'known'    => ! empty( $ams['known'] ),
                    'material' => isset( $ams['material'] ) ? sanitize_text_field( $ams['material'] ) : '',
                    'color'    => isset( $ams['color'] ) ? sanitize_text_field( $ams['color'] ) : '',
                    'detail'   => isset( $ams['detail'] ) ? sanitize_text_field( $ams['detail'] ) : '',
                ),
                'camera_available' => ! empty( $p['camera_available'] ),
            );
        }
    }
    update_option( 'c3d_bridge_status', array(
        'bridge_name'  => sanitize_text_field( isset( $body['bridge_name'] ) ? $body['bridge_name'] : '' ),
        'version'      => sanitize_text_field( isset( $body['version'] ) ? $body['version'] : '' ),
        'queue_length' => isset( $body['queue_length'] ) ? intval( $body['queue_length'] ) : 0,
        'printers'     => $printers,
        'reported_at'  => current_time( 'mysql' ),
    ) );
    update_option( 'c3d_lan_bridge_heartbeat', current_time( 'mysql' ) );
    return c3d_bridge_ok();
}

/**
 * GET /bridge/jobs — queued print jobs for the bridge to carry out.
 * Single-bridge assumption (v1): all queued jobs are returned; each
 * bridge executes only jobs naming printers it knows. Only physical
 * jobs are ever queued by the dashboard; digital lines are skipped
 * at queue time and never appear here.
 */
function c3d_bridge_jobs( $request ) {
    $file_map = get_option( 'c3d_print_file_map', array() );
    if ( ! is_array( $file_map ) ) {
        $file_map = array();
    }
    $orders = get_posts( array(
        'post_type'   => 'shop_order',
        'post_status' => array( 'wc-processing', 'wc-on-hold' ),
        'meta_key'    => '_c3d_print_jobs',
        'numberposts' => -1,
    ) );
    $jobs = array();
    foreach ( $orders as $order_post ) {
        $stored = get_post_meta( $order_post->ID, '_c3d_print_jobs', true );
        if ( ! is_array( $stored ) ) {
            continue;
        }
        foreach ( $stored as $job_key => $job ) {
            if ( ! is_array( $job ) || 'queued' !== ( isset( $job['status'] ) ? $job['status'] : '' ) ) {
                continue;
            }
            $items = array();
            if ( isset( $job['items'] ) && is_array( $job['items'] ) ) {
                foreach ( $job['items'] as $item ) {
                    if ( is_array( $item ) && ! empty( $item['name'] ) ) {
                        $items[] = array(
                            'name' => sanitize_text_field( $item['name'] ),
                            'qty'  => isset( $item['qty'] ) ? intval( $item['qty'] ) : 1,
                        );
                    }
                }
            }
            $first_name = count( $items ) > 0 ? $items[0]['name'] : '';
            $file_url = isset( $job['file_url'] ) ? $job['file_url'] : '';
            if ( '' === $file_url && '' !== $first_name && isset( $file_map[ $first_name ] ) ) {
                $file_url = $file_map[ $first_name ];
            }
            $jobs[] = array(
                'job_key'        => sanitize_text_field( $job_key ),
                'order_id'       => intval( $order_post->ID ),
                'printer_name'   => sanitize_text_field( isset( $job['printer'] ) ? $job['printer'] : '' ),
                'items'          => $items,
                'color'          => sanitize_text_field( isset( $job['color'] ) ? $job['color'] : '' ),
                'material'       => '',
                'safety_verdict' => sanitize_text_field( isset( $job['safety']['overall'] ) ? $job['safety']['overall'] : 'held' ),
                'file_url'       => esc_url_raw( $file_url ),
                'file_name'      => '' !== $file_url ? sanitize_file_name( basename( wp_parse_url( $file_url, PHP_URL_PATH ) ) ) : '',
                'file_sha256'    => '',
            );
        }
    }
    return rest_ensure_response( array( 'jobs' => $jobs ) );
}

/**
 * POST /bridge/job-report — per-job progress/completion/failure
 * from the bridge. Updates the job's status in order meta and adds
 * an order note. A snapshot (base64 JPEG, <= 2 MB) is stored with
 * the failed-print log when one was actually captured.
 */
function c3d_bridge_job_report( $request ) {
    $body = $request->get_json_params();
    if ( ! is_array( $body ) ) {
        return new WP_Error( 'c3d_bridge_bad_body', 'Expected a JSON body.', array( 'status' => 400 ) );
    }
    $order_id = isset( $body['order_id'] ) ? intval( $body['order_id'] ) : 0;
    $job_key  = isset( $body['job_key'] ) ? sanitize_text_field( $body['job_key'] ) : '';
    $status   = isset( $body['status'] ) ? sanitize_text_field( $body['status'] ) : '';
    if ( $order_id <= 0 || '' === $job_key || '' === $status ) {
        return new WP_Error( 'c3d_bridge_bad_body', 'order_id, job_key, and status are required.', array( 'status' => 400 ) );
    }
    $jobs = get_post_meta( $order_id, '_c3d_print_jobs', true );
    if ( ! is_array( $jobs ) || ! isset( $jobs[ $job_key ] ) || ! is_array( $jobs[ $job_key ] ) ) {
        return new WP_Error( 'c3d_bridge_not_found', 'No such print job on that order.', array( 'status' => 404 ) );
    }
    $jobs[ $job_key ]['status'] = $status;
    $jobs[ $job_key ]['bridge_reported_at'] = current_time( 'mysql' );
    if ( isset( $body['progress_percent'] ) && is_numeric( $body['progress_percent'] ) ) {
        $jobs[ $job_key ]['progress_percent'] = floatval( $body['progress_percent'] );
    }
    if ( isset( $body['reason'] ) ) {
        $jobs[ $job_key ]['bridge_reason'] = sanitize_text_field( $body['reason'] );
    }
    update_post_meta( $order_id, '_c3d_print_jobs', $jobs );

    if ( isset( $body['snapshot_base64'] ) && is_string( $body['snapshot_base64'] ) ) {
        // ~2.8M base64 chars ~= 2 MB of JPEG. Anything larger is
        // rejected rather than stored; the report itself still lands.
        if ( strlen( $body['snapshot_base64'] ) <= 2800000 ) {
            $clean = preg_replace( '/[^A-Za-z0-9+\/=]/', '', $body['snapshot_base64'] );
            update_post_meta( $order_id, '_c3d_failure_snapshot_' . $job_key, $clean );
        }
    }

    if ( function_exists( 'wc_get_order' ) ) {
        $order = wc_get_order( $order_id );
        if ( $order ) {
            $note = sprintf(
                'Bridge report for print job %s on %s: %s.%s',
                $job_key,
                isset( $body['printer_name'] ) ? sanitize_text_field( $body['printer_name'] ) : 'unknown printer',
                $status,
                isset( $body['reason'] ) && '' !== $body['reason'] ? ' ' . sanitize_text_field( $body['reason'] ) : ''
            );
            $order->add_order_note( $note );
        }
    }
    return c3d_bridge_ok();
}

add_action( 'rest_api_init', function () {
    register_rest_route( 'c3d-shipping/v1', '/bridge/heartbeat', array(
        'methods'             => 'POST',
        'callback'            => 'c3d_bridge_heartbeat',
        'permission_callback' => 'c3d_bridge_check_key',
    ) );
    register_rest_route( 'c3d-shipping/v1', '/bridge/status', array(
        'methods'             => 'POST',
        'callback'            => 'c3d_bridge_status',
        'permission_callback' => 'c3d_bridge_check_key',
    ) );
    register_rest_route( 'c3d-shipping/v1', '/bridge/jobs', array(
        'methods'             => 'GET',
        'callback'            => 'c3d_bridge_jobs',
        'permission_callback' => 'c3d_bridge_check_key',
    ) );
    register_rest_route( 'c3d-shipping/v1', '/bridge/job-report', array(
        'methods'             => 'POST',
        'callback'            => 'c3d_bridge_job_report',
        'permission_callback' => 'c3d_bridge_check_key',
    ) );
} );

/**
 * Bridge key admin page (under the C3D Shipping menu).
 * Generate = new key, shown ONCE, old key stops working at once.
 * Revoke = no key at all; every bridge request gets 401.
 */
function c3d_bridge_key_page() {
    if ( ! current_user_can( 'manage_options' ) ) {
        wp_die( esc_html__( 'You do not have permission to view this page.' ) );
    }
    $message = '';
    if ( isset( $_POST['c3d_bridge_key_action'] ) && check_admin_referer( 'c3d_bridge_key_action' ) ) {
        $action = sanitize_text_field( wp_unslash( $_POST['c3d_bridge_key_action'] ) );
        if ( 'generate' === $action ) {
            $raw = rtrim( strtr( base64_encode( random_bytes( 48 ) ), '+/', '-_' ), '=' );
            update_option( 'c3d_bridge_key_hash', hash_hmac( 'sha256', $raw, wp_salt() ) );
            update_option( 'c3d_bridge_key_once', $raw );
            $message = 'New bridge key generated. Copy it now — it will not be shown again after this page load.';
        } elseif ( 'revoke' === $action ) {
            delete_option( 'c3d_bridge_key_hash' );
            delete_option( 'c3d_bridge_key_once' );
            $message = 'Bridge key revoked. The bridge can no longer reach the dashboard until a new key is generated and stored on the PC.';
        }
    }

    // Shown-once key: read it, display it, then delete it in the
    // same request so a refresh cannot show it again.
    $once = get_option( 'c3d_bridge_key_once', '' );
    if ( '' !== $once ) {
        delete_option( 'c3d_bridge_key_once' );
    }
    $configured = '' !== get_option( 'c3d_bridge_key_hash', '' );
    $heartbeat = get_option( 'c3d_lan_bridge_heartbeat', '' );
    $status = get_option( 'c3d_bridge_status', array() );

    echo '<div class="wrap"><h1>C3D Bridge Key</h1>';
    if ( '' !== $message ) {
        echo '<div class="notice notice-success"><p>' . esc_html( $message ) . '</p></div>';
    }
    if ( '' !== $once ) {
        echo '<h2>Your new bridge key (copy it now)</h2>';
        echo '<p><code style="font-size:15px;word-break:break-all;">' . esc_html( $once ) . '</code></p>';
        echo '<p>On the shop PC, run <code>C3DBridge.exe setup</code> and paste this key when asked. It is stored only in a restricted local file on that PC.</p>';
    }
    echo '<p>Bridge key configured: <strong>' . ( $configured ? 'yes' : 'no' ) . '</strong></p>';
    echo '<p>Last bridge heartbeat: <strong>' . esc_html( '' !== $heartbeat ? $heartbeat : 'never' ) . '</strong></p>';
    if ( is_array( $status ) && ! empty( $status['printers'] ) ) {
        echo '<p>Last reported printers: ';
        $bits = array();
        foreach ( $status['printers'] as $p ) {
            $bits[] = esc_html( $p['name'] ) . ' (' . esc_html( $p['state'] ) . ')';
        }
        echo implode( ', ', $bits ) . '</p>';
    }
    echo '<form method="post">';
    wp_nonce_field( 'c3d_bridge_key_action' );
    echo '<p><button type="submit" name="c3d_bridge_key_action" value="generate" class="button button-primary">Generate new key</button> ';
    echo '<button type="submit" name="c3d_bridge_key_action" value="revoke" class="button">Revoke key</button></p>';
    echo '<p>Generating a new key immediately invalidates the old one — update the PC with <code>C3DBridge.exe setup</code> right after.</p>';
    echo '</form></div>';
}

add_action( 'admin_menu', function () {
    add_submenu_page(
        'c3d-shipping',
        'Bridge Key',
        'Bridge key',
        'manage_options',
        'c3d-bridge-key',
        'c3d_bridge_key_page'
    );
} );

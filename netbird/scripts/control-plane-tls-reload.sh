#!/bin/bash
# Netbird control plane: reload renewed TLS certificates and publish days-to-expiry.
# Runs daily on the control-plane instance as an SSM State Manager association
# (NetbirdControlPlaneStack.cs, TlsReloadAssociation).
#
# Why (2026-10-07 outage): the dashboard container's certbot renews the Let's Encrypt certificate in
# the shared netbird-letsencrypt volume, but management (:33073) and signal (:10000) read their
# --cert-file once at startup and never reload it. After a renewal they keep serving the old
# certificate from memory until it expires. Every new client handshake then fails with
# "tls: bad certificate" and the desktop client hangs on Connect without ever reaching the SSO login.
# The certificate was renewed on 2026-09-07, but management and signal served the July certificate
# until it expired on 2026-10-07.
#
# For each service:
#   1. Compare the certificate the service serves with the one on disk (SHA-256 fingerprint).
#   2. If they differ and the disk certificate is a genuine renewal (trusted, issued for $DOMAIN,
#      valid now, matching private key, expiring later than the served one), `docker restart` that
#      container (same container and config, no recreate) and verify it now serves the disk
#      certificate.
#   3. Treat any served certificate with less than MIN_DAYS left as a failure: it means renewal has
#      stopped, weeks before anything expires.
#   4. Publish TlsReloadFailures (0 or 1) and the lowest TlsCertDaysToExpiry to CloudWatch. The stack
#      alarms on a failure, and on no datapoint at all (the job stopped running).
# Any check that cannot be completed is a failure, never a silent pass.
set -uo pipefail

DOMAIN="${NB_TLS_DOMAIN:-netbird.autoguru.com.au}"
COMPOSE_PROJECT="${NB_TLS_COMPOSE_PROJECT:-artifacts}"
# <compose service>:<host port>:<reload|check>. The dashboard is only measured: its own certbot
# reloads its nginx after a renewal.
SERVICES="${NB_TLS_SERVICES:-management:33073:reload signal:10000:reload dashboard:443:check}"
PUBLISH_METRIC="${NB_TLS_PUBLISH_METRIC:-true}"
# certbot renews 30 days before expiry and this job reloads within a day, so under 14 days left
# means renewal or reload has failed.
MIN_DAYS=14
REGION=ap-southeast-2
NAMESPACE=Netbird/ControlPlane

failed=0
min_days=""

log() { echo "$*"; logger -t netbird-tls-reload -- "$*" 2>/dev/null || true; }
fail() { log "ERROR: $*"; failed=1; }
track_days() { if [ -z "$min_days" ] || [ "$1" -lt "$min_days" ]; then min_days=$1; fi; }

# Leaf certificate (PEM) a local listener serves right now; empty if nothing answers.
served_cert() {
  timeout 15 openssl s_client -connect "127.0.0.1:$1" -servername "$DOMAIN" </dev/null 2>/dev/null \
    | openssl x509 2>/dev/null
}
fingerprint() { openssl x509 -noout -fingerprint -sha256 2>/dev/null | cut -d= -f2; }
expiry_epoch() {
  local end
  end=$(openssl x509 -noout -enddate 2>/dev/null | cut -d= -f2)
  [ -n "$end" ] && date -d "$end" +%s
}

for spec in $SERVICES; do
  IFS=: read -r svc port mode <<<"$spec"

  pem=$(served_cert "$port")
  served_fp=$(fingerprint <<<"$pem")
  if [ -z "$served_fp" ]; then
    fail "$svc: no certificate served on port $port"
    track_days 0
    continue
  fi

  if [ "$mode" = reload ]; then
    cid=$(docker ps -q --filter "label=com.docker.compose.project=$COMPOSE_PROJECT" \
      --filter "label=com.docker.compose.service=$svc")
    le_dir=""
    if [ "$(wc -w <<<"$cid")" -eq 1 ]; then
      le_dir=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/etc/letsencrypt"}}{{.Source}}{{end}}{{end}}' "$cid")
    fi
    disk_cert="$le_dir/live/$DOMAIN/fullchain.pem"
    disk_key="$le_dir/live/$DOMAIN/privkey.pem"
    disk_fp=""
    [ -n "$le_dir" ] && disk_fp=$(fingerprint <"$disk_cert")

    if [ "$(wc -w <<<"$cid")" -ne 1 ]; then
      fail "$svc: expected one running container in project $COMPOSE_PROJECT, found '${cid//$'\n'/ }'"
    elif [ -z "$disk_fp" ]; then
      fail "$svc: cannot read the certificate on disk ($disk_cert)"
    elif [ "$served_fp" = "$disk_fp" ]; then
      log "$svc: serving the current certificate"
    else
      cert_pub=$(openssl x509 -noout -pubkey <"$disk_cert" 2>/dev/null)
      key_pub=$(openssl pkey -pubout -in "$disk_key" 2>/dev/null)
      disk_exp=$(expiry_epoch <"$disk_cert")
      served_exp=$(expiry_epoch <<<"$pem")
      # Only a genuine renewal is worth a restart. Anything else would swap a working (if ageing)
      # listener for a broken one, so it is reported instead. `openssl verify` checks the chain
      # against the system trust store, the validity dates and the hostname in one go.
      if ! openssl verify -purpose sslserver -verify_hostname "$DOMAIN" \
          -untrusted "$disk_cert" "$disk_cert" >/dev/null 2>&1; then
        fail "$svc: certificate on disk is untrusted, expired or not issued for $DOMAIN; not restarting"
      elif [ -z "$cert_pub" ] || [ "$cert_pub" != "$key_pub" ]; then
        fail "$svc: certificate on disk does not match its private key; not restarting"
      elif [ -z "$disk_exp" ] || [ -z "$served_exp" ] || [ "$disk_exp" -le "$served_exp" ]; then
        fail "$svc: certificate on disk does not expire later than the served one; not restarting"
      else
        log "$svc: serving $served_fp but disk has renewed $disk_fp; restarting container $cid"
        timeout 120 docker restart "$cid" >/dev/null
        if [ "$(docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null)" != true ]; then
          log "$svc: container $cid is not running after the restart; starting it"
          timeout 60 docker start "$cid" >/dev/null
        fi
        if [ "$(docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null)" != true ]; then
          fail "$svc: container $cid is not running after restart and start"
        else
          reloaded=false
          for _ in 1 2 3 4 5 6 7 8 9 10 11 12; do
            sleep 5
            pem=$(served_cert "$port")
            if [ "$(fingerprint <<<"$pem")" = "$disk_fp" ]; then reloaded=true; break; fi
          done
          if [ "$reloaded" = true ]; then
            log "$svc: now serving the renewed certificate"
          else
            fail "$svc: still not serving the renewed certificate 60s after the restart"
          fi
        fi
      fi
    fi
  fi

  exp=$(expiry_epoch <<<"$pem")
  if [ -z "$exp" ]; then
    fail "$svc: cannot read the expiry of the served certificate"
    track_days 0
    continue
  fi
  days=$(( (exp - $(date +%s)) / 86400 ))
  log "$svc: served certificate expires in $days days"
  track_days "$days"
done

min_days="${min_days:-0}"
if [ "$min_days" -lt "$MIN_DAYS" ]; then
  fail "a served certificate expires in $min_days days (threshold $MIN_DAYS): renewal is not happening"
fi

if [ "$PUBLISH_METRIC" = true ]; then
  # If this call fails nothing is published, and the stack's no-datapoint alarm fires instead.
  if aws cloudwatch put-metric-data --region "$REGION" --namespace "$NAMESPACE" --metric-data \
      "MetricName=TlsReloadFailures,Value=$failed,Unit=Count" \
      "MetricName=TlsCertDaysToExpiry,Value=$min_days,Unit=Count"; then
    log "published $NAMESPACE TlsReloadFailures=$failed TlsCertDaysToExpiry=$min_days"
  else
    fail "could not publish metrics to $NAMESPACE"
  fi
fi

exit "$failed"

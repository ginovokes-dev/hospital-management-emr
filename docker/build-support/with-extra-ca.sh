#!/bin/sh
# Runs a command. Behind a company proxy that re-signs HTTPS traffic the build cannot download anything unless it trusts the proxy's
# certificate: put the certificate (a .crt file) into docker/build-support/ca/ and it is trusted here as well as the normal ones.
if ls /extra-ca/*.crt >/dev/null 2>&1; then
  SYS=/etc/ssl/certs/ca-certificates.crt; [ -f "$SYS" ] || SYS=/dev/null
  cat "$SYS" /extra-ca/*.crt > /tmp/ca-bundle.crt
  export SSL_CERT_FILE=/tmp/ca-bundle.crt NODE_EXTRA_CA_CERTS=/tmp/ca-bundle.crt CURL_CA_BUNDLE=/tmp/ca-bundle.crt
fi
exec "$@"

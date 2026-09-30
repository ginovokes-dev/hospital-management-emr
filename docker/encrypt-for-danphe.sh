#!/usr/bin/env bash
# Encrypts a value the way Danphe stores passwords and licence dates (RBAC.EncryptPassword):
# 3DES-ECB, key = MD5("Danphesalt") (16 bytes = two-key 3DES), PKCS7 padding, base64.
#   usage: encrypt-for-danphe.sh <plain text>
set -euo pipefail
[ $# -eq 1 ] || { echo "usage: $0 <plain text>" >&2; exit 2; }
KEY=$(printf 'Danphesalt' | md5sum | cut -d' ' -f1)
printf '%s' "$1" | openssl enc -des-ede-ecb -K "$KEY" -nosalt | base64 -w0

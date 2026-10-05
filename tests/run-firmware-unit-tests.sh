#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_dir="$(mktemp -d "${TMPDIR:-/tmp}/logicscope-fw-tests.XXXXXX")"
trap 'rm -rf "$test_dir"' EXIT
cxx="${CXX:-g++}"

"$cxx" -std=c++17 -Wall -Wextra -Werror -pedantic \
  -I"$repo_root/firmware/include" \
  "$repo_root/firmware/src/trigger.cpp" \
  "$repo_root/tests/firmware_trigger_test.cpp" \
  -o "$test_dir/trigger-test"
"$test_dir/trigger-test"

"$cxx" -std=c++17 -Wall -Wextra -Werror -pedantic \
  -I"$repo_root/firmware/include" \
  "$repo_root/tests/firmware_pin_map_test.cpp" \
  -o "$test_dir/pin-map-test"
"$test_dir/pin-map-test"

"$cxx" -std=c++17 -Wall -Wextra -Werror -pedantic \
  -I"$repo_root/tests/native_stubs" \
  -I"$repo_root/firmware/include" \
  "$repo_root/firmware/src/protocol.cpp" \
  "$repo_root/firmware/src/usb_tx.cpp" \
  "$repo_root/tests/firmware_protocol_test.cpp" \
  -o "$test_dir/protocol-test"
"$test_dir/protocol-test"

echo "Firmware trigger, channel-map, command protocol, and USB packet unit tests passed."

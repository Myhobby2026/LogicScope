#include <cassert>
#include <cstdint>
#include <vector>

#include "protocol.h"
#include "usb_tx.h"

FakeSerialPort Serial;

using LogicScope::HostCommand;
using LogicScope::HostCommandKind;
using LogicScope::protocolInit;
using LogicScope::protocolPoll;
using LogicScope::protocolSendCaps;
using LogicScope::protocolSendStatus;
using LogicScope::usbTxResetSequence;
using LogicScope::usbTxSendSamples;
using LogicScope::usbTxSendEmptyFinal;
using LogicScope::kDataFlagFinal;
using LogicScope::kDataFlagTriggered;

static void Push(const std::vector<uint8_t>& bytes) {
  Serial.input.insert(Serial.input.end(), bytes.begin(), bytes.end());
}

int main() {
  protocolInit();
  HostCommand command;

  Push({0x02});
  assert(protocolPoll(command));
  assert(command.kind == HostCommandKind::GetCaps);

  Push({0x00});
  assert(protocolPoll(command));
  assert(command.kind == HostCommandKind::Stop);

  Push({0x03});
  assert(protocolPoll(command));
  assert(command.kind == HostCommandKind::SelfTest);

  // START waits for all eight payload bytes and decodes each field little-endian.
  Push({0x01, 0x80, 0x84});
  assert(!protocolPoll(command));
  Push({0x1E, 0x00, 0x01, 0x03, 0x01, 0x19});
  assert(protocolPoll(command));
  assert(command.kind == HostCommandKind::Start);
  assert(command.rateHz == 2'000'000);
  assert(command.mode == 1);
  assert(command.triggerChannel == 3);
  assert(command.triggerEdge == 1);
  assert(command.preTriggerPercent == 25);

  Push({0xFE});
  assert(protocolPoll(command));
  assert(command.kind == HostCommandKind::Invalid);

  Serial.output.clear();
  protocolSendCaps(20'000'000, 10'000'000, 131'072);
  const std::vector<uint8_t> caps = {
      0x00, 0x2D, 0x31, 0x01, 0x80, 0x96, 0x98, 0x00,
      0x00, 0x00, 0x02, 0x00, 1, 0, 0, 16};
  assert(Serial.output == caps);

  Serial.output.clear();
  protocolSendStatus(4, 3);
  assert((Serial.output == std::vector<uint8_t>{0x55, 4, 3}));

  Serial.output.clear();
  usbTxResetSequence();
  const uint16_t sampleWords[] = {0x1234, 0xABCD};
  assert(usbTxSendSamples(sampleWords, 2, 1, kDataFlagTriggered, true));
  const std::vector<uint8_t> dataPacket = {
      0xAA, 0, 1, 3, 2, 0, 0, 0, 0x34, 0x12, 0xCD, 0xAB};
  assert(Serial.output == dataPacket);
  assert(usbTxSendEmptyFinal(1, 0));
  const std::vector<uint8_t> emptyFinal = {
      0xAA, 1, 1, kDataFlagFinal, 0, 0, 0, 0};
  assert(std::vector<uint8_t>(Serial.output.begin() + dataPacket.size(),
                              Serial.output.end()) == emptyFinal);
  return 0;
}

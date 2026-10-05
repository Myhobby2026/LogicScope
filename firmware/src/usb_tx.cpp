#include "usb_tx.h"

namespace LogicScope {
namespace {

constexpr uint32_t kHeaderBytes = 8;
constexpr uint32_t kWriteTimeoutMs = 1000;
uint8_t g_sequence = 0;
uint8_t g_packet[kHeaderBytes + kMaxPacketSamples * sizeof(uint16_t)]
    __attribute__((aligned(32)));

bool writeExact(const uint8_t* bytes, size_t length) {
  size_t offset = 0;
  const uint32_t start = millis();
  while (offset < length) {
    if (!Serial) return false;
    const size_t written = Serial.write(bytes + offset, length - offset);
    offset += written;
    if (written == 0) {
      if (static_cast<uint32_t>(millis() - start) >= kWriteTimeoutMs) return false;
      yield();
    }
  }
  return true;
}

bool sendPacket(const uint16_t* samples, uint32_t count, uint8_t mode,
                uint8_t flags) {
  g_packet[0] = 0xAA;
  g_packet[1] = g_sequence;
  g_packet[2] = mode;
  g_packet[3] = flags;
  g_packet[4] = static_cast<uint8_t>(count);
  g_packet[5] = static_cast<uint8_t>(count >> 8);
  g_packet[6] = static_cast<uint8_t>(count >> 16);
  g_packet[7] = static_cast<uint8_t>(count >> 24);
  for (uint32_t i = 0; i < count; ++i) {
    const uint16_t sample = samples[i];
    g_packet[kHeaderBytes + i * 2u] = static_cast<uint8_t>(sample);
    g_packet[kHeaderBytes + i * 2u + 1u] = static_cast<uint8_t>(sample >> 8);
  }
  if (!writeExact(g_packet, kHeaderBytes + count * 2u)) return false;
  ++g_sequence;  // Wrap modulo 256 by construction.
  return true;
}

}  // namespace

void usbTxResetSequence() { g_sequence = 0; }

bool usbTxSendSamples(const uint16_t* samples, uint32_t count, uint8_t mode,
                      uint8_t flags, bool finalPacket) {
  uint32_t offset = 0;
  if (count == 0) {
    return finalPacket && sendPacket(nullptr, 0, mode,
                                     static_cast<uint8_t>(flags | kDataFlagFinal));
  }
  while (offset < count) {
    const uint32_t packetSamples = min(kMaxPacketSamples, count - offset);
    const bool last = offset + packetSamples == count;
    uint8_t packetFlags = flags;
    if (finalPacket && last) packetFlags |= kDataFlagFinal;
    if (!sendPacket(samples + offset, packetSamples, mode, packetFlags)) return false;
    offset += packetSamples;
  }
  return true;
}

bool usbTxSendEmptyFinal(uint8_t mode, uint8_t flags) {
  return sendPacket(nullptr, 0, mode, static_cast<uint8_t>(flags | kDataFlagFinal));
}

}  // namespace LogicScope

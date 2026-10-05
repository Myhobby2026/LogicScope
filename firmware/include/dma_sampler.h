#pragma once

#include <Arduino.h>

namespace LogicScope {

// Four paired DMA slots. At 8,192 samples/slot, each bank occupies 128 KiB;
// both banks and the scatter/gather TCDs fit in Teensy 4.1 OCRAM with room for
// the core's USB buffers. A 16K-slot ring is not possible in that OCRAM budget.
constexpr uint16_t kDmaBlockSamples = 8192;
constexpr uint8_t kDmaBlockCount = 4;

struct RawBlockView {
  const volatile uint32_t* gpio6;
  const volatile uint32_t* gpio7;
  uint16_t samples;
  uint8_t slot;
};

bool dmaSamplerInit();
bool dmaSamplerStart(uint32_t requestedRateHz, uint32_t maxRateHz,
                     uint32_t& actualRateHz);
void dmaSamplerStop();
bool dmaSamplerTryAcquire(RawBlockView& view);
void dmaSamplerRelease(uint8_t slot);
bool dmaSamplerIsRunning();
bool dmaSamplerTakeOverrun();
uint32_t dmaSamplerOverrunCount();

}  // namespace LogicScope

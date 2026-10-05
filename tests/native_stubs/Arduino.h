#pragma once

#include <cstddef>
#include <cstdint>
#include <deque>
#include <vector>

using std::uint8_t;
using std::uint16_t;
using std::uint32_t;

class FakeSerialPort {
 public:
  explicit operator bool() const { return connected; }
  int available() const { return static_cast<int>(input.size()); }
  int read() {
    if (input.empty()) return -1;
    const int value = input.front();
    input.pop_front();
    return value;
  }
  std::size_t write(const uint8_t* bytes, std::size_t count) {
    output.insert(output.end(), bytes, bytes + count);
    return count;
  }

  bool connected = true;
  std::deque<uint8_t> input;
  std::vector<uint8_t> output;
};

extern FakeSerialPort Serial;

inline uint32_t millis() { return 0; }
inline void yield() {}
template <typename T>
constexpr T min(T left, T right) { return left < right ? left : right; }

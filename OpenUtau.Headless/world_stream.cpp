#include "worldline/worldline.h"
#include "worldline/model/effects.h"
#include "world/synthesisrealtime.h"

#include <algorithm>
#include <cmath>
#include <memory>
#include <vector>

namespace {
struct Noise {
  uint32_t x = 123456789, y = 362436069, z = 521288629, w = 88675123;
  uint32_t Next() {
    const uint32_t t = x ^ (x << 11);
    x = y; y = z; z = w;
    w = (w ^ (w >> 19)) ^ (t ^ (t >> 8));
    return w;
  }
};
thread_local Noise* current_noise = nullptr;

struct NoiseScope {
  Noise* previous;
  explicit NoiseScope(Noise& noise) : previous(current_noise) { current_noise = &noise; }
  ~NoiseScope() { current_noise = previous; }
};

struct Frames {
  std::vector<double> spectrum, aperiodicity;
  std::vector<double*> spectrum_rows, aperiodicity_rows;
};

struct Stream {
  WorldSynthesizer synth{};
  Noise noise;
  std::unique_ptr<Frames> frames[4];
  Stream() { InitializeSynthesizer(44100, 220.0 * 1000 / 44100, 2048, 512, 4, &synth); }
  ~Stream() { DestroySynthesizer(&synth); }
};
}  // namespace

// WORLD's xorshift normal approximation with per-stream state. The native
// dependency retains WORLD's BSD-3-Clause notice. Analysis and other streams
// cannot reset this stream's noise sequence while lazy assets are acquired.
extern "C" double WorldStreamRandom() {
  thread_local Noise fallback;
  auto& noise = current_noise ? *current_noise : fallback;
  uint32_t sum = 0;
  for (int index = 0; index < 12; ++index) sum += noise.Next() >> 4;
  return sum / 268435456.0 - 6.0;
}

// Each stream retains pulse phase and overlap-add state across all parameter
// pages. Calls into WORLD, including source analysis, must be serialized by the
// caller because the upstream noise generator uses process-wide state.
extern "C" DLL_API void* WorldStreamCreate() {
  try {
    return new Stream();
  } catch (...) { return nullptr; }
}

// Copies at most one second of parameters. Returns 1 when queued, 0 when the
// four-page queue is full, and -1 for invalid inputs or allocation failure.
extern "C" DLL_API int WorldStreamAdd(void* stream, double* f0, int count,
    const double* spectrum, const double* aperiodicity, double timbre,
    double tension, double breathiness, double voicing) {
  if (!stream || !f0 || !spectrum || !aperiodicity || count < 2 || count > 201 ||
      !std::isfinite(timbre) || timbre < 0 || timbre > 1 ||
      !std::isfinite(tension) || tension < 0 || tension > 1 ||
      !std::isfinite(breathiness) || breathiness < 0 || breathiness > 1 ||
      !std::isfinite(voicing) || voicing < 0 || voicing > 1) return -1;
  auto& state = *static_cast<Stream*>(stream);
  if (state.synth.head_pointer - state.synth.current_pointer2 == 4) return 0;
  try {
    constexpr int width = 1025;
    auto page = std::make_unique<Frames>();
    page->spectrum.assign(spectrum, spectrum + count * width);
    page->aperiodicity.resize(count * width);
    page->spectrum_rows.resize(count);
    page->aperiodicity_rows.resize(count);
    const double breath = breathiness > .5 ? breathiness * 4 - 1 : breathiness * 2;
    for (int frame = 0; frame < count; ++frame) {
      if (!std::isfinite(f0[frame]) || f0[frame] < 0 || f0[frame] > 20000) return -1;
      auto* sp = page->spectrum.data() + frame * width;
      auto* ap = page->aperiodicity.data() + frame * width;
      worldline::ShiftGender(sp, width, std::lround((timbre - .5) * 200));
      auto coefficients = worldline::GetTensionCoefficients(
          f0[frame], 44100, std::lround((tension - .5) * 200), width);
      for (int bin = 0; bin < width; ++bin) {
        double ratio = std::clamp(aperiodicity[frame * width + bin], .001, .999999);
        double noise = sp[bin] * (f0[frame] == 0 ? 1 : ratio * ratio) * breath * breath;
        double periodic = f0[frame] == 0 ? 0 :
            sp[bin] * (1 - ratio * ratio) * coefficients[bin] * voicing * voicing;
        sp[bin] = std::max(1e-16, periodic + noise);
        ap[bin] = std::sqrt(std::clamp(noise / sp[bin], .000001, .999999));
      }
      page->spectrum_rows[frame] = sp;
      page->aperiodicity_rows[frame] = ap;
    }
    const int slot = state.synth.head_pointer % 4;
    state.frames[slot] = std::move(page);
    return AddParameters(f0, count, state.frames[slot]->spectrum_rows.data(),
        state.frames[slot]->aperiodicity_rows.data(), &state.synth);
  } catch (...) { return -1; }
}

// Writes exactly 512 samples when ready, zero when more parameters are needed.
// The output buffer must contain at least 512 doubles.
extern "C" DLL_API int WorldStreamRead(void* stream, double* output) {
  if (!stream || !output) return -1;
  auto& state = *static_cast<Stream*>(stream);
  try {
    NoiseScope noise(state.noise);
    if (!Synthesis2(&state.synth)) return 0;
    std::copy_n(state.synth.buffer, 512, output);
    return 512;
  } catch (...) { return -1; }
}

extern "C" DLL_API void WorldStreamDestroy(void* stream) {
  delete static_cast<Stream*>(stream);
}

// Blix wrapper around bc7enc: encode a whole RGBA8 image to raw BC7 blocks,
// parallelised across block-rows. Exposed as C for P/Invoke from
// Blix.Tools.Cook (see Bc7Native.cs). bc7enc.c itself is single-block; this
// adds the tiling + threading the cook needs to BC7-encode 4K textures in
// seconds instead of the minutes BCnEncoder.Net took.
#include "bc7enc.h"

#include <thread>
#include <vector>
#include <mutex>
#include <algorithm>
#include <cstdint>
#include <cstddef>

extern "C" {

// bc7enc_compress_block_init() builds global lookup tables and MUST run once
// before any encode. Guarded so repeated calls (and concurrent first-encodes)
// are safe.
static std::once_flag g_init_flag;
static void ensure_init() { std::call_once(g_init_flag, [] { bc7enc_compress_block_init(); }); }

void blix_bc7_init() { ensure_init(); }

// Encode a tightly-packed width*height RGBA8 image (R first) into dst as raw
// BC7 blocks, row-major over 4x4 blocks: dst size = ceil(w/4)*ceil(h/4)*16.
// perceptual != 0 uses YCbCr weights (good for sRGB color maps); 0 uses linear
// weights (normal / data maps). quality: 0 = fastest, 1 = balanced, 2 = high.
// num_threads <= 0 means single-threaded (the cook parallelises across textures
// already, so it passes 1 to avoid core oversubscription).
void blix_bc7_encode(
    const uint8_t* src, int width, int height,
    uint8_t* dst, int perceptual, int quality, int num_threads)
{
    ensure_init();

    bc7enc_compress_block_params params;
    bc7enc_compress_block_params_init(&params);
    if (perceptual)
        bc7enc_compress_block_params_init_perceptual_weights(&params);
    else
        bc7enc_compress_block_params_init_linear_weights(&params);

    switch (quality)
    {
        case 0: // fastest: disable mode-1 partition search + least-squares.
            params.m_max_partitions_mode = 0;
            params.m_uber_level = 0;
            params.m_try_least_squares = BC7ENC_FALSE;
            break;
        case 1: // balanced (default).
            params.m_max_partitions_mode = 16;
            params.m_uber_level = 0;
            break;
        default: // high quality.
            params.m_max_partitions_mode = BC7ENC_MAX_PARTITIONS1;
            params.m_uber_level = 1;
            break;
    }

    const int blocksX = (width + 3) / 4;
    const int blocksY = (height + 3) / 4;

    auto encode_rows = [&](int by0, int by1)
    {
        uint8_t block[64]; // 16 RGBA texels
        for (int by = by0; by < by1; ++by)
        {
            for (int bx = 0; bx < blocksX; ++bx)
            {
                // Gather the 4x4 texel block, clamping at the image edge so
                // non-multiple-of-4 mips (and the 4x4 floor) stay well-defined.
                for (int y = 0; y < 4; ++y)
                {
                    const int sy = std::min(by * 4 + y, height - 1);
                    for (int x = 0; x < 4; ++x)
                    {
                        const int sx = std::min(bx * 4 + x, width - 1);
                        const uint8_t* s = src + (static_cast<size_t>(sy) * width + sx) * 4;
                        uint8_t* d = block + (y * 4 + x) * 4;
                        d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; d[3] = s[3];
                    }
                }
                uint8_t* out = dst + (static_cast<size_t>(by) * blocksX + bx) * BC7ENC_BLOCK_SIZE;
                bc7enc_compress_block(out, block, &params);
            }
        }
    };

    if (num_threads <= 1 || blocksY <= 1)
    {
        encode_rows(0, blocksY);
        return;
    }

    const int threads = std::min(num_threads, blocksY);
    const int rowsPer = (blocksY + threads - 1) / threads;
    std::vector<std::thread> pool;
    pool.reserve(threads);
    for (int t = 0; t < threads; ++t)
    {
        const int y0 = t * rowsPer;
        const int y1 = std::min(blocksY, y0 + rowsPer);
        if (y0 >= y1) break;
        pool.emplace_back(encode_rows, y0, y1);
    }
    for (auto& th : pool) th.join();
}

} // extern "C"

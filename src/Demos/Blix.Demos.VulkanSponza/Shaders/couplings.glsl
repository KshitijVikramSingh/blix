// A patch coupling (SponzaLoop.SunNodesSolve.cs, PackCoupling): one word -- the patch it reads in the low 20 bits, its
// weight in the high 12 as 2^(24 c / 4095 - 24) (c the code; ~0.2% at most, the weights k / rays from 1 / 1024 up), 8 B a
// coupling before (Crytek Sponza's 45 M: 346 -> 173 MB, 2026-10-24).
#ifndef COUPLINGS_GLSL
#define COUPLINGS_GLSL
uint couplingTo(uint c) { return c & 0xFFFFFu; }
float couplingWeight(uint c) { return exp2(float(c >> 20) * (24.0 / 4095.0) - 24.0); }
#endif

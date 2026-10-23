"""Two seeds of one corridor run against each other (uv run tools/gi-model/seed_noise.py TAG ...): cnTAG.log and csTAG.log,
the gate's corridor points (corr.sh) of a run and of the same run with BLIX_GATHER_SEED=1000. Per kind (surface, seam):
the |error| against the reference, the noise (the seeds' difference / sqrt 2) and the two seeds averaged against the
reference (the bias, with what noise is left in the average)."""
import re,sys
S=re.compile(r'(surface|seam)\s+at (\S+) .* total (-?[\d.]+) ref (-?[\d.]+)')
def get(p): return {(m.group(1),m.group(2)):(float(m.group(3)),float(m.group(4))) for m in map(S.search,open(p)) if m and float(m.group(3))>=0}
for tag in sys.argv[1:]:
    a,b=get(f'cn{tag}.log'),get(f'cs{tag}.log')
    for kind in ('surface','seam'):
        ks=[k for k in a if k in b and k[0]==kind]; mr=sum(a[k][1] for k in ks)/len(ks)
        noise=sum(abs(a[k][0]-b[k][0]) for k in ks)/len(ks)/mr/2**0.5
        mean=[(a[k][0]+b[k][0])/2 for k in ks]; bias=sum(abs(m-a[k][1]) for m,k in zip(mean,ks))/len(ks)/mr
        err=(sum(abs(a[k][0]-a[k][1]) for k in ks)+sum(abs(b[k][0]-b[k][1]) for k in ks))/2/len(ks)/mr
        print(f'{tag:8s} {kind:8s} |error| {err:.4f}  noise (seed to seed) {noise:.4f}  the two seeds averaged {bias:.4f}')

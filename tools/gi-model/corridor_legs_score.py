import numpy as np, json, sys
from pathlib import Path
HS='/private/tmp/blix-dbg/hs'
def load(tag):
    d=sorted(Path(f'{HS}/dump-{tag}').glob('view-*'))[-1]; m=json.loads((d/'meta.json').read_text()); n=m['texelCount']; rv=m['refView']
    ids=np.frombuffer((d/'reference_records.u32').read_bytes(),np.uint32).astype(np.int64)
    a4=np.frombuffer((d/'reference_view.f32x4').read_bytes(),np.float32).reshape(-1,4)[rv['height']*rv['width']:][:len(ids)]
    E=(a4[:,:3]/np.maximum(a4[:,3:4],1))@np.array([.2126,.7152,.0722])
    rw=np.frombuffer((d/'resolved.u32x2').read_bytes(),np.uint32).reshape(n,2)[ids,0].astype(np.int64)
    sc=np.exp2((rw>>27)-24.0); G=np.stack([(rw&511)*sc,((rw>>9)&511)*sc,((rw>>18)&511)*sc],-1)@np.array([.2126,.7152,.0722])
    t=np.frombuffer((d/'texels.f32x8').read_bytes(),np.float32).reshape(-1,8)[:n]
    return E.astype(float),G,t[ids,:3],t[ids,4:7]
rows={}
tags=sys.argv[1:]
for tg in tags:
    E,G,P,N=load(tg); x,y,z=P.T; ok=E>1e-7
    leg2=(x>8.15)&(z<1.05)&ok; leg1=(z>0.95)&ok
    bins=[('leg 1',leg1),('leg 2, 0-0.5 m',leg2&(z>0.5)),('leg 2, 0.5-1.5 m',leg2&(z<=0.5)&(z>-0.5)),('leg 2, 1.5-2.5 m',leg2&(z<=-0.5)&(z>-1.5)),('leg 2, last 0.5 m',leg2&(z<=-1.5))]
    for lab,s in bins:
        r=G[s]/E[s]; rows.setdefault(lab,[]).append(f"{np.median(r):6.2f} ({s.sum():3d}, ref {np.median(E[s]):.5f})")
    for nm,s0 in (('floor',N[:,1]>0.9),('ceiling',N[:,1]<-0.9),('far wall',N[:,0]<-0.9),('corner block',N[:,0]>0.9),('dead end',N[:,2]>0.9)):
        s=leg2&(z<=-0.5)&s0; r=G[s]/E[s]
        rows.setdefault('  deep leg 2: '+nm,[]).append(f"{np.median(r):6.2f} ({s.sum():3d}, ref {np.median(E[s]):.5f})" if s.sum()>2 else ' '*28)
print(f"{'median renderer/reference':24s}"+"".join(f"{t:>30s}" for t in tags))
for k,v in rows.items(): print(f"{k:24s}"+"".join(f"{c:>30s}" for c in v))

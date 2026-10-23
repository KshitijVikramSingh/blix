import numpy as np, json
from pathlib import Path
HS='/private/tmp/blix-dbg/hs'
z=np.load(f'{HS}/atlas_ratios.npz')
def load(tag):
    d=sorted(Path(f'{HS}/dump-{tag}').glob('view-*'))[-1]; m=json.loads((d/'meta.json').read_text()); n=m['texelCount']; rv=m['refView']
    ids=np.frombuffer((d/'reference_records.u32').read_bytes(),np.uint32).astype(np.int64)
    a4=np.frombuffer((d/'reference_view.f32x4').read_bytes(),np.float32).reshape(-1,4)[rv['height']*rv['width']:][:len(ids)]
    E=(a4[:,:3]/np.maximum(a4[:,3:4],1))@np.array([.2126,.7152,.0722])
    rw=np.frombuffer((d/'resolved.u32x2').read_bytes(),np.uint32).reshape(n,2)[ids,0].astype(np.int64)
    sc=np.exp2((rw>>27)-24.0); G=np.stack([(rw&511)*sc,((rw>>9)&511)*sc,((rw>>18)&511)*sc],-1)@np.array([.2126,.7152,.0722])
    t=np.frombuffer((d/'texels.f32x8').read_bytes(),np.float32).reshape(-1,8)[:n]
    return ids,E.astype(float),G,t[ids,:3]
import sys
va=sys.argv[1:] or ['gref0','gref1000']
i0,E0,G0,P=load(va[0]); i1,E1,G1,_=load(va[1])
assert (i0==i1).all()
pos={v:k for k,v in enumerate(z['ids'])}; k=np.array([pos[i] for i in i0])
ra=z['all'][k]; rn=z['near'][k]; mv=z['moved'][k]
G=(G0+G1)/2; E=E0; ok=E>1e-6
extra=[]
for j in range(2,len(va),2):
    _,_,a_,_=load(va[j]); _,_,b_,_=load(va[j+1]); extra.append((va[j],(a_+b_)/2))
print(f"reference: {len(i0)} records; seed-to-seed |dG|/G median {np.median(np.abs(G0-G1)/np.maximum(G,1e-9))/np.sqrt(2):.3f}")
def region(p):
    x,y,zz=p
    if abs(x+1)<0.1 and zz<-5: return 'slot'
    if 0.9<x<3.1 and 2.45<zz<2.83: return 'narrow gap'
    if x>8.1 and zz<-1.5: return 'dead end'
    if x>5.1: return 'corridor'
    return 'courtyard'
reg=np.array([region(p) for p in P])
def stats(sel,lab):
    if sel.sum()<3: return
    out=[]
    for name,g in [(va[0],G),('atlas model',G*ra)]+extra:
        r=g[sel]/E[sel]
        out.append(f"{name} {g[sel].sum()/E[sel].sum():.3f} (median {np.median(r):.3f}, |err| {np.mean(np.abs(r-1)):.3f})")
    print(f"  {lab:34s} {sel.sum():4d}: "+" | ".join(out))
for grp,gs in (('moved down (<0.85x)',mv&(ra<1)),('moved up (>1.15x)',mv&(ra>1)),('control (±2%)',~mv)):
    print(grp)
    stats(ok&gs,'all')
    for r_ in ('slot','narrow gap','dead end','corridor','courtyard'): stats(ok&gs&(reg==r_),r_)

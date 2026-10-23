# The atlas-addressed lookup, modelled on the fine round's first pass (captured with each hit's distance and the ray's
# weight): a hit answered by the live record on its own face -- the one whose atlas texel the hit's lightmap UV lands
# in, here the nearest live record in the hit's plane, facing the ray, within REACH -- instead of its cell or the search.
# Run: uv run --with numpy --with scipy python tools/gi-model/atlas_lookup.py (dumps cd0/cd1 under /private/tmp/blix-dbg/hs:
# recon.sh with BLIX_CAPTURE_FINE_ONLY=1 --lean-shade --ray-capture 2 --ray-capture-room 16777216 --fine-rays 512, seeds 0, 1000).
# Crevices corridor, 2026-10-23: 99.8% of cell hits find a live record on their face; with the hit's own direct sun kept,
# the hits' light 1.00x, gate points unchanged (seam |error| 0.0271 -> 0.0271, surface 0.0128 -> 0.0127); 10% of
# texels move over 15%, in the slot, the narrow gap and the dead end, where a 20 cm cell spans a gap of 8-12 cm.
import numpy as np, json, re
from pathlib import Path
from scipy.spatial import cKDTree
HS='/private/tmp/blix-dbg/hs'
def dump(tag): return sorted(Path(f'{HS}/dump-'+tag).glob('view-*'))[-1]
d=dump('cd0'); m=json.loads((d/'meta.json').read_text()); n=m['texelCount']
t=np.frombuffer((d/'texels.f32x8').read_bytes(),np.float32).reshape(-1,8)[:n]
P,N,W=t[:,:3].astype(np.float64),t[:,4:7].astype(np.float64),t[:,3]
N/=np.maximum(np.linalg.norm(N,axis=1,keepdims=True),1e-9)
live=np.abs(W)>=2
pt=np.frombuffer((d/'patch.i32').read_bytes(),np.int32)[:n]
def u9(w):
    w=w.astype(np.int64); sc=np.exp2((w>>27)-24.0); return np.stack([(w&511)*sc,((w>>9)&511)*sc,((w>>18)&511)*sc],-1)@np.array([.2126,.7152,.0722])
def resolved(tag): return u9(np.frombuffer((dump(tag)/'resolved.u32x2').read_bytes(),np.uint32).reshape(n,2)[:,0])
tree=cKDTree(P)
def oct(w):
    o=np.stack([(w&0xFFFF).astype(np.int16),(w>>16).astype(np.uint16).astype(np.int16)],1)/32767.0
    z=1-np.abs(o).sum(1); x,y=o[:,0].copy(),o[:,1].copy(); neg=z<0
    x[neg],y[neg]=(1-np.abs(o[neg,1]))*np.sign(o[neg,0]),(1-np.abs(o[neg,0]))*np.sign(o[neg,1])
    v=np.stack([x,y,z],1); return v/np.linalg.norm(v,axis=1,keepdims=True)
REACH=0.06
def onface(hp,dirs):
    best=np.full(len(hp),-1); bur=np.zeros(len(hp),bool)
    for s in range(0,len(hp),300000):
        q=hp[s:s+300000]; dq=dirs[s:s+300000]
        dd,nn=tree.query(q,k=24,distance_upper_bound=REACH)
        b=np.full(len(q),-1); bb=np.zeros(len(q),bool)
        for j in range(nn.shape[1]):
            cand=nn[:,j]; ok=cand<n; cc=np.where(ok,cand,0)
            f=ok&(np.abs(np.einsum('ij,ij->i',q-P[cc],N[cc]))<0.012)&(np.einsum('ij,ij->i',dq,N[cc])<0)
            bb|=f&~live[cc]; take=f&live[cc]&(b<0); b[take]=cc[take]
        best[s:s+300000]=b; bur[s:s+300000]=bb
    return best,bur
def red(hp): return (hp[:,0]>8.19)&(hp[:,0]<9.21)&(hp[:,1]<0.61)&(hp[:,2]>-2.01)&(hp[:,2]<-1.59)
def run(tag):
    raw=(dump(tag)/'rays_captured.u32x8').read_bytes(); cnt=np.frombuffer(raw[:4],np.uint32)[0]
    R=np.frombuffer(raw[16:],np.uint32).reshape(-1,8)[:cnt]
    ids=R[:,0].astype(np.int64); kind=(R[:,6]>>8)&0xFF
    dist=np.array(R[:,6]>>16,np.uint16).view(np.float16).astype(np.float64)
    L=np.stack([R[:,2],R[:,3],R[:,4]],1).copy().view(np.float32).astype(np.float64)@np.array([.2126,.7152,.0722])
    w=R[:,5].copy().view(np.float32).astype(np.float64)
    dirs=oct(R[:,1]).astype(np.float64)
    c0=np.maximum(np.einsum('ij,ij->i',dirs,N[ids]),0)
    c=L*c0                                  # the ray's contribution
    hp=P[ids]+N[ids]*0.01+dirs*np.where(kind>0,dist,0)[:,None]
    alb=np.where(red(hp),0.184,0.25)
    E=np.where(kind>0,c*np.pi/np.maximum(alb*w,1e-12),0)   # the light the hit's answer stood for
    at=np.full(len(R),-1); bur=np.zeros(len(R),bool)
    h=np.flatnonzero(kind>0); at[h],bur[h]=onface(hp[h],dirs[h])
    # the hit's direct sun (its own shadow ray, the same whichever lookup answers it): irradiance x n.l, seen where the
    # answer holds over half of it (the indirect light is far below it)
    toSun=np.array(m['sunToward'],np.float64); toSun/=np.linalg.norm(toSun); irr=float(np.mean(m['sunIrradiance']))
    hn=np.where((at>=0)[:,None],N[np.maximum(at,0)],-dirs)
    direct=irr*np.maximum(hn@toSun,0)
    D=np.where((kind>0)&(E>0.5*direct)&(direct>0.05),direct,0.0)
    return dict(ids=ids,kind=kind,dist=dist,c=c,w=w,alb=alb,E=E,D=D,near=R[:,7].astype(np.int64),at=at,bur=bur,res=resolved(tag))
A=[run('cd0'),run('cd1')]
names={1:'cell',2:'near texel',3:'borrowed'}
for s,a in enumerate(A):
    tot=len(a['c']); print(f"\n== seed {s}: {tot:,} rays over {np.unique(a['ids']).size:,} texels")
    for k in (1,2,3):
        mk=a['kind']==k
        if not mk.any(): continue
        print(f"  {names[k]:10s} {mk.mean():6.1%} of rays (dist median {np.median(a['dist'][mk]):.2f} m): atlas finds a live record on the face {np.mean(a['at'][mk]>=0):.1%}, only buried ones {np.mean((a['at'][mk]<0)&a['bur'][mk]):.1%}, none {np.mean((a['at'][mk]<0)&~a['bur'][mk]):.1%}")
    mk=(a['kind']==2)&(a['near']<n)
    r=a['res'][a['near'][mk]]; ok=r>1e-6
    q=a['E'][mk][ok]/r[ok]
    print(f"  self-check, near-texel hits: recovered light / their record's resolved: median {np.median(q):.3f} (p10 {np.percentile(q,10):.3f}, p90 {np.percentile(q,90):.3f})")
    mk2=mk&(a['at']>=0)
    print(f"  near-texel hits: the atlas record is the search's {np.mean(a['at'][mk2]==a['near'][mk2]):.1%}, within 5 cm of it {np.mean(np.linalg.norm(P[a['at'][mk2]]-P[a['near'][mk2]],axis=1)<0.051):.1%}, same patch {np.mean(pt[a['at'][mk2]]==pt[a['near'][mk2]]):.1%}")
    a['k']=np.median(q)
    Dx=a['E'][mk]-a['res'][a['near'][mk]]
    print(f"  direct-sun check, near-texel hits: classified sunlit {np.mean(a['D'][mk]>0):.1%}, exactly {np.mean(Dx>0.05):.1%}; classified minus exact |median| {np.median(np.abs(a['D'][mk]-np.maximum(Dx,0))):.4f}, sum ratio {a['D'][mk].sum()/np.maximum(Dx,0).sum():.3f}")
# variants: a hit's answer E -> E' ; contribution scales by E'/E (misses and unanswered hits keep theirs)
def variant(a,which):
    E2=a['E'].copy()
    ok=(a['at']>=0)&(a['kind']>0)
    if which=='near': ok&=a['dist']<0.25
    E2[ok]=a['res'][a['at'][ok]]*a['k']+a['D'][ok]
    c2=np.where(a['E']>0,a['c']*E2/np.maximum(a['E'],1e-12),np.where(ok,a['alb']*a['w']/np.pi*E2,a['c']))
    return c2
for which in ('near','all'):
    print(f"\n-- atlas for {'hits nearer than 25 cm' if which=='near' else 'every hit'}:")
    rat=[]
    for s,a in enumerate(A):
        c2=variant(a,which)
        T=np.bincount(a['ids'],a['c'],minlength=n); T2=np.bincount(a['ids'],c2,minlength=n); cntt=np.bincount(a['ids'],minlength=n)
        sel=cntt>0; r_=T2[sel]/np.maximum(T[sel],1e-12); rat.append((T,T2,cntt))
        for k in (1,2):
            mk=(a['kind']==k)&(a['at']>=0)&((a['dist']<0.25) if which=='near' else True)
            print(f"  seed {s} {names[k]:10s} hits re-answered: their light {c2[mk].sum()/max(a['c'][mk].sum(),1e-12):.3f} x")
        print(f"  seed {s}: texels' light ratio median {np.median(r_):.3f} (p10 {np.percentile(r_,10):.3f}, p90 {np.percentile(r_,90):.3f})")
    # the gate points: each point's records scaled by their light ratio (seed by seed), against the reference
    lt=np.frombuffer((d/'lightmap_texel.r32u').read_bytes(),np.uint32).reshape(1080,-1)
    f=m['files']; aw,ah=f['lightmap_index.r32u']['width'],f['lightmap_index.r32u']['height']
    idx=np.frombuffer((d/'lightmap_index.r32u').read_bytes(),np.uint32).reshape(ah,aw)
    S=re.compile(r'(seam|surface)\s+at (\S+),(\S+)\s+gpu\s+total (-?[\d.]+) ref (-?[\d.]+)')
    out={}
    for s,tag in enumerate(('cd0','cd1')):
        T,T2,cntt=rat[s]
        for mm in map(S.search,open(f'{HS}/{tag}.log')):
            if not mm: continue
            g,ref=float(mm.group(4)),float(mm.group(5))
            if g<0 or ref<=0: continue
            x=min(int(float(mm.group(2))*1920),1919); y=min(int(float(mm.group(3))*1080),1079); v=lt[y,x]
            fx=(v&0xFFFF)/8-0.5; fy=(v>>16)/8-0.5; x0=int(np.floor(fx)); y0=int(np.floor(fy))
            rs=[int(idx[min(max(y0+dy,0),ah-1),min(max(x0+dx,0),aw-1)]) for dx in (0,1) for dy in (0,1)]
            rs=[r for r in rs if r<n]
            sc=np.mean([T2[r]/T[r] if cntt[r]>0 and T[r]>0 else 1.0 for r in rs]) if rs else 1.0
            out.setdefault((mm.group(1),mm.group(2),mm.group(3)),[]).append((g,g*sc,ref))
    for kind_ in ('surface','seam'):
        ks=[k for k,v in out.items() if k[0]==kind_ and len(v)==2]
        mr=np.mean([out[k][0][2] for k in ks])
        for j,lab in ((0,'now'),(1,'atlas')):
            e=np.mean([abs((out[k][0][j]+out[k][1][j])/2-out[k][0][2]) for k in ks])/mr
            nz=np.mean([abs(out[k][0][j]-out[k][1][j]) for k in ks])/mr/np.sqrt(2)
            en=np.sum([(out[k][0][j]+out[k][1][j])/2 for k in ks])/np.sum([out[k][0][2] for k in ks])
            print(f"  {kind_:7s} {lab:5s}: two-seed mean |error| {e:.4f}, noise {nz:.4f}, energy {en:.4f}  ({len(ks)} points)")

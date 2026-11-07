"""Strict screen for statically dead functions in a stripped arm64 libunity.so (hosts for kfpatch fail-safe veneers).

Usage: python scripts/re/find_dead_functions.py <libunity.so> <out.json> [minsize=96]   (needs numpy; layout constants are for Unity 2018.4.11f1 arm64)
Replaces the 2.11.804 first-pass finder, which used a 4-insn adrp window and wrongly accepted 0xA08DDC (a registered callback).
Prove any candidate with scripts/re/verify_dead_host.py before hosting code in it (AGENTS.md section 3 rule 7). This is still a static proof only.
"""
import sys,struct,json,numpy as np
so=sys.argv[1]; out=sys.argv[2]; MIN=int(sys.argv[3]) if len(sys.argv)>3 else 96
d=open(so,'rb').read()
CS,CE=0xd9ec0,0xf01538   # .plt + .text
EH=0x106f2cc
cnt=struct.unpack_from('<I',d,EH+8)[0]
tbl=np.frombuffer(d,dtype='<i4',count=cnt*2,offset=EH+12).reshape(-1,2)
starts=tbl[:,0].astype(np.int64)+EH; fdes=tbl[:,1].astype(np.int64)+EH
o=np.argsort(starts); starts=starts[o]; fdes=fdes[o]
rng=np.array([struct.unpack_from('<I',d,int(f)+12)[0] for f in fdes],dtype=np.int64); ends=starts+rng
w=np.frombuffer(d,dtype='<u4',count=(CE-CS)//4,offset=CS).astype(np.int64); addr=CS+4*np.arange(len(w))
T=[];SRC=[]
def add(t,s): T.append(np.asarray(t,dtype=np.int64)); SRC.append(np.asarray(s,dtype=np.int64))
m=((w>>26)==5)|((w>>26)==0x25); imm=w[m]&0x3ffffff; imm=np.where(imm&0x2000000,imm-(1<<26),imm); add(addr[m]+imm*4,addr[m])
m=((w>>24)==0x54)|(((w>>25)&0x3f)==0x1a); imm=(w[m]>>5)&0x7ffff; imm=np.where(imm&0x40000,imm-(1<<19),imm); add(addr[m]+imm*4,addr[m])
m=(((w>>25)&0x3f)==0x1b); imm=(w[m]>>5)&0x3fff; imm=np.where(imm&0x2000,imm-(1<<14),imm); add(addr[m]+imm*4,addr[m])
m=((w&0x9f000000)==0x10000000); im=(((w[m]>>5)&0x7ffff)<<2)|((w[m]>>29)&3); im=np.where(im&0x100000,im-(1<<21),im); add(addr[m]+im,addr[m])
m=((w&0x3b000000)==0x18000000); imm=(w[m]>>5)&0x7ffff; imm=np.where(imm&0x40000,imm-(1<<19),imm); add(addr[m]+imm*4,addr[m])
# adrp: for each adrp, scan next 64 insns; record page-resolved addr for add/ldr/str with Rn==Rd (until Rd is overwritten by a non-derived write); also record the bare page when no consumer found (conservative: page itself)
idx=np.nonzero((w&0x9f000000)==0x90000000)[0]
tg=[];sr=[]
for i in idx.tolist():
    x=int(w[i]); rd=x&31
    im=(((x>>5)&0x7ffff)<<2)|((x>>29)&3)
    if im&0x100000: im-=1<<21
    pg=((CS+4*i)>>12<<12)+(im<<12)
    regs={rd:pg}   # tracked regs holding page+offset
    for j in range(i+1,min(i+65,len(w))):
        y=int(w[j]); rn=(y>>5)&31; r=y&31
        if (y&0x7f800000)==0x11000000 and rn in regs and (y>>31)==1:   # add/sub x,x,#imm
            i12=(y>>10)&0xfff
            if (y>>22)&1: i12<<=12
            a=regs[rn]+(i12 if not (y>>30)&1 else -i12)
            tg.append(a); sr.append(CS+4*i)
            regs[r]=a
            continue
        if (y&0x3b000000)==0x39000000 and rn in regs:  # ldr/str uimm
            sc=(y>>30)&3
            if (y&0x04000000) and ((y>>23)&1) and sc==0: sc=4
            tg.append(regs[rn]+(((y>>10)&0xfff)<<sc)); sr.append(CS+4*i)
        if (y&0x7fe0ffe0)==0x2a0003e0 and ((y>>16)&31) in regs and (y>>31)==1:  # mov x,x
            regs[r]=regs[(y>>16)&31]; continue
        if r in regs and not ((y&0x3b000000)==0x39000000 and False):
            # destination written by something else -> stop tracking that reg (stores have no Rd write; approximate: loads/ALU write Rd)
            if (y&0x3f000000)!=0x39000000 or ((y>>22)&3)!=0:
                if (y&0x3b000000)!=0x39000000 or ((y>>22)&3)!=0: regs.pop(r,None)
        if not regs: break
    tg.append(pg); sr.append(-1)   # bare page (never counted as ref; used for diagnostics only)
add(tg,sr)
TT=np.concatenate(T); SS=np.concatenate(SRC)
keep=(SS>=0)&(TT>=CS)&(TT<CE+0x10000000); TT=TT[keep]; SS=SS[keep]
# non-code refs
q=np.frombuffer(d[:len(d)//8*8],dtype='<u8'); q=q[(q>=CS)&(q<CE)].astype(np.int64)
secs=[]
shoff=struct.unpack_from('<Q',d,0x28)[0]; shes,shn,shs=struct.unpack_from('<HHH',d,0x3a)
for i in range(shn): secs.append(struct.unpack_from('<IIQQQQIIQQ',d,shoff+i*shes))
RS=[s for s in secs if s[1]==4]; ds=[s for s in secs if s[1]==11][0]
dsym=np.frombuffer(d,dtype=np.dtype([('n','<u4'),('i','u1'),('o','u1'),('s','<u2'),('v','<u8'),('z','<u8')]),count=ds[5]//24,offset=ds[4])
R=[]
for s in RS:
    r=np.frombuffer(d,dtype=np.dtype([('o','<u8'),('i','<u8'),('a','<i8')]),count=s[5]//24,offset=s[4]); sym=(r['i']>>32).astype(np.int64)
    base=np.where(sym>0,dsym['v'][np.clip(sym,0,len(dsym)-1)].astype(np.int64),0); R.append(base+r['a'])
R=np.concatenate(R); dv=dsym['v'][dsym['s']!=0].astype(np.int64)
data_ref=np.concatenate([q,R,dv])
# 32-bit rel-table scan targets (all non-text sections), k<64 bases, scales 1 and 4 -> collect candidate targets
rt=[]
for nm_off,nm_sz in [(s[4],s[5]) for s in secs if s[3]>=0xf01540 and s[1]==1 and s[3]<0x1320000]:
    if nm_off==0 or nm_off in (0x106f2cc,0x10e6588): continue   # .eh_frame_hdr/.eh_frame are pc-relative by design (descriptive only)
    arr=np.frombuffer(d,dtype='<i4',count=nm_sz//4,offset=nm_off).astype(np.int64)
    va=[s[3] for s in secs if s[4]==nm_off and s[1]==1][0]; A=va+4*np.arange(len(arr))
    for sc in (1,4):
        for k in range(32):
            t=A-4*k+arr*sc; rt.append(t[(t>=CS)&(t<CE)])
rt=np.concatenate(rt)
def fidx(a): return np.searchsorted(starts,a,side='right')-1
ext=np.zeros(len(starts),dtype=np.int64)
fi=fidx(TT); ok=(fi>=0)&(TT<ends[np.clip(fi,0,None)]); si=fidx(SS)
np.add.at(ext,fi[ok&(fi!=si)],1)
for arr in (data_ref,rt):
    f=fidx(arr); okk=(f>=0)&(arr<ends[np.clip(f,0,None)]); np.add.at(ext,f[okk],1)
cand=np.nonzero((ext==0)&(rng>=MIN))[0]
res=[(int(starts[i]),int(rng[i])) for i in cand]
json.dump(res,open(out,'w'))
print("functions",len(starts),"with 0 external refs (strict):",len(cand),"of which >=",MIN,"B")
for s,z in res[:200]: print(hex(s),z)

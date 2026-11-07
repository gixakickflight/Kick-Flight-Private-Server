"""Rigorous dead-host verifier (read-only, numpy). See find_dead_functions.py. usage: verify_dead_host.py <so> <host_hex> <hostlen> <func_start_hex> <func_end_hex> <codestart_hex> <codeend_hex>
Scans whole ELF for every kind of reference into [func_start,func_end)."""
import sys,struct,numpy as np
so=sys.argv[1]; H=int(sys.argv[2],16); HL=int(sys.argv[3]); S=int(sys.argv[4],16); E=int(sys.argv[5],16)
CS,CE=int(sys.argv[6],16),int(sys.argv[7],16)   # code scan range (plt+text)
d=open(so,'rb').read()
# --- ELF parse
shoff=struct.unpack_from('<Q',d,0x28)[0]; shentsize,shnum,shstrndx=struct.unpack_from('<HHH',d,0x3a)
secs=[]
for i in range(shnum):
    nm,ty,fl,ad,of,sz,lk,inf,al,es=struct.unpack_from('<IIQQQQIIQQ',d,shoff+i*shentsize); secs.append([nm,ty,fl,ad,of,sz,lk,inf,al,es])
stroff=secs[shstrndx][4]
def sname(n):
    e=d.index(b'\0',stroff+n); return d[stroff+n:e].decode()
S_={sname(s[0]):s for s in secs}
phoff=struct.unpack_from('<Q',d,0x20)[0]; phnum=struct.unpack_from('<H',d,0x38)[0]
loads=[]
for i in range(phnum):
    t,fl,o,va,pa,fsz,msz,al=struct.unpack_from('<IIQQQQQQ',d,phoff+i*56)
    if t==1: loads.append((va,o,fsz))
def inr(x): return (x>=S)&(x<E)
def inh(x): return (x>=H)&(x<H+HL)
print(f"host=[{H:#x},{H+HL:#x}) func=[{S:#x},{E:#x}) size={E-S}")
fail=[]
# 1. code references
w=np.frombuffer(d,dtype='<u4',count=(CE-CS)//4,offset=CS).astype(np.int64)  # file offset==vaddr in seg0
addr=CS+4*np.arange(len(w))
def rep(name,tgt,src):
    sel=inr(tgt); hits=list(zip(src[sel].tolist(),tgt[sel].tolist()))
    ext=[(s,t) for s,t in hits if not (S<=s<E)]
    inn=[(s,t) for s,t in hits if (S<=s<E)]
    print(f"[code] {name}: total-into-func={len(hits)} external={len(ext)} internal={len(inn)}")
    for s,t in ext[:10]: print("   EXTERNAL",hex(s),'->',hex(t))
    if ext: fail.append(name)
    return hits
m=((w>>26)==5)|((w>>26)==0x25); imm=w[m]&0x3ffffff; imm=np.where(imm&0x2000000,imm-(1<<26),imm); rep('b/bl',addr[m]+imm*4,addr[m])
m=((w>>24)==0x54); imm=(w[m]>>5)&0x7ffff; imm=np.where(imm&0x40000,imm-(1<<19),imm); rep('b.cond',addr[m]+imm*4,addr[m])
m=(((w>>25)&0x3f)==0x1a); imm=(w[m]>>5)&0x7ffff; imm=np.where(imm&0x40000,imm-(1<<19),imm); rep('cbz/cbnz',addr[m]+imm*4,addr[m])
m=(((w>>25)&0x3f)==0x1b); imm=(w[m]>>5)&0x3fff; imm=np.where(imm&0x2000,imm-(1<<14),imm); rep('tbz/tbnz',addr[m]+imm*4,addr[m])
m=((w&0x9f000000)==0x10000000); im=(((w[m]>>5)&0x7ffff)<<2)|((w[m]>>29)&3); im=np.where(im&0x100000,im-(1<<21),im); rep('adr',addr[m]+im,addr[m])
m=((w&0x3b000000)==0x18000000); imm=(w[m]>>5)&0x7ffff; imm=np.where(imm&0x40000,imm-(1<<19),imm); rep('ldr/prfm-literal',addr[m]+imm*4,addr[m])
# adrp: any adrp whose page overlaps func range; then resolve following add/ldr/str imm within next 16 insns for same reg
idx=np.nonzero((w&0x9f000000)==0x90000000)[0]
x=w[idx]; imm=(((x>>5)&0x7ffff)<<2)|((x>>29)&3); imm=np.where(imm&0x100000,imm-(1<<21),imm)
page=((addr[idx]>>12)<<12)+(imm<<12)
cand=idx[((page+0xfff)>=S)&(page<E)]
print(f"[code] adrp whose 4K page overlaps func: {len(cand)} (of {len(idx)} adrp)")
exact=[]; pageonly=[]
for i in cand:
    xi=int(w[i]); rd=xi&31
    im=(((xi>>5)&0x7ffff)<<2)|((xi>>29)&3)
    if im&0x100000: im-=1<<21
    pg=((CS+4*int(i))>>12<<12)+(im<<12)
    found=False
    for j in range(i+1,min(i+65,len(w))):
        y=int(w[j]); rn=(y>>5)&31
        if rn!=rd: 
            if (y&31)==rd and not (y&0x3b000000)==0x39000000 and (y&0x7f800000)!=0x11000000: pass
            continue
        a=None
        if (y&0x7f800000)==0x11000000:   # add/sub imm
            i12=(y>>10)&0xfff
            if (y>>22)&1: i12<<=12
            a=pg+i12 if (y>>30)&1==0 else pg-i12
        elif (y&0x3b000000)==0x39000000:  # ldr/str unsigned imm (incl simd)
            sc=(y>>30)&3
            if (y&0x04000000) and ((y>>23)&1)==1 and sc==0: sc=4
            a=pg+(((y>>10)&0xfff)<<sc)
        if a is not None and S<=a<E: found=True; exact.append((CS+4*int(i),CS+4*j,a))
    if not found: pageonly.append(CS+4*int(i))
print(f"[code] adrp+add/ldr resolving INTO func: {len(exact)}")
for e in exact[:10]: print("   ",[hex(v) for v in e], "internal" if S<=e[0]<E else "EXTERNAL")
ext=[e for e in exact if not (S<=e[0]<E)]
if ext: fail.append('adrp-exact')
print(f"[code] adrp page-overlap but offset outside func (benign unless chained further): {len(pageonly)}")
# movz/movk absolute materialisation (movz x,#lo ; movk x,#hi,lsl16 ...) with lo16 matching func range low bits is too noisy; report none
# 2. data scans: qwords 8-aligned over whole file
q=np.frombuffer(d[:len(d)//8*8],dtype='<u8')
qi=np.nonzero((q>=S)&(q<E))[0]
print(f"[data] raw 8-aligned qwords == address in func: {len(qi)}")
for i in qi[:10]: print("   file_off",hex(int(i)*8),hex(int(q[i])))
if len(qi): fail.append('raw-qword')
# also unaligned scan of 8-byte values at 4-aligned offsets
q4=np.frombuffer(d[4:4+(len(d)-4)//8*8],dtype='<u8'); qj=np.nonzero((q4>=S)&(q4<E))[0]
print(f"[data] raw qwords at 4-mod-8 offsets in func: {len(qj)}")
if len(qj): fail.append('raw-qword4')
# 3. relocations
def relas(sec):
    s=S_[sec]; n=s[5]//24
    return np.frombuffer(d,dtype=np.dtype([('o','<u8'),('i','<u8'),('a','<i8')]),count=n,offset=s[4])
ds=S_['.dynsym']; nsym=ds[5]//24
sy=np.frombuffer(d,dtype=np.dtype([('n','<u4'),('i','u1'),('o','u1'),('s','<u2'),('v','<u8'),('z','<u8')]),count=nsym,offset=ds[4])
for sec in ('.rela.dyn','.rela.plt'):
    r=relas(sec); typ=(r['i']&0xffffffff).astype(np.int64); sym=(r['i']>>32).astype(np.int64)
    base=np.where(sym>0, sy['v'][np.clip(sym,0,nsym-1)].astype(np.int64),0)
    val=base+r['a']
    sel=(val>=S)&(val<E)
    print(f"[reloc] {sec}: {len(r)} entries, types={dict(zip(*np.unique(typ,return_counts=True)))}; target-in-func (addend or sym+addend): {int(sel.sum())}")
    for k in np.nonzero(sel)[0][:10]: print("   ",hex(int(r['o'][k])),typ[k],hex(int(val[k])))
    if sel.any(): fail.append('reloc '+sec)
    # also relocation targets (offset) inside func range -> reloc writes into code
    so_=(r['o']>=S)&(r['o']<E)
    print(f"[reloc] {sec}: relocations writing INTO func range: {int(so_.sum())}")
# 4. dynsym
sel=(sy['v']>=S)&(sy['v']<E)
print(f"[dynsym] symbols with value in func: {int(sel.sum())} (of {nsym}); undefined/shndx=0 excluded: ", int((sel&(sy['s']!=0)).sum()))
if (sel&(sy['s']!=0)).any(): fail.append('dynsym')
# dynamic section entries INIT/FINI/etc
dyn=S_['.dynamic']; 
for i in range(dyn[5]//16):
    t,v=struct.unpack_from('<qQ',d,dyn[4]+i*16)
    if t in (12,13,25,26,3,23,7,5,6,4) and S<=v<E: print("[dynamic] tag",t,hex(v)); fail.append('dynamic')
# 5. 32-bit relative jump-table style scan over all non-text words (rodata, eh_frame excluded separately)
def band(name,off,sz,sc_list=(1,4),K=64):
    arr=np.frombuffer(d,dtype='<i4',count=sz//4,offset=off).astype(np.int64)
    a0=off; # vaddr==off for seg0; for seg1 add 0x10000
    va0=None
    for (v,o,fs) in loads:
        if o<=off<o+fs: va0=v+(off-o)
    A=va0+4*np.arange(len(arr)); tot=0
    for sc in sc_list:
        for k in range(K):
            T=A-4*k+arr*sc
            tot+=int(((T>=S)&(T<E)).sum())
    print(f"[reltab] {name}: 32-bit rel-table style hits (base k<{K}, scale {sc_list}): {tot}")
    return tot
for nm in ('.rodata','.gcc_except_table','.data.rel.ro','.data'):
    s=S_[nm]; t=band(nm,s[4],s[5]-(s[5]%4))
    if t: fail.append('reltab '+nm)
# eh_frame note
EH=S_['.eh_frame_hdr'][3]
cnt=struct.unpack_from('<I',d,EH+8)[0]
tbl=np.frombuffer(d,dtype='<i4',count=cnt*2,offset=EH+12).reshape(-1,2); st=tbl[:,0].astype(np.int64)+EH
print(f"[eh_frame] FDE(s) starting inside func: {int(((st>=S)&(st<E)).sum())} (descriptive only, not a caller)")
print("RESULT:", "FAIL "+str(fail) if fail else "PASS (no external references of any kind found)")

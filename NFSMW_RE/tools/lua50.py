import struct
OPS="MOVE LOADK LOADBOOL LOADNIL GETUPVAL GETGLOBAL GETTABLE SETGLOBAL SETUPVAL SETTABLE NEWTABLE SELF ADD SUB MUL DIV POW UNM NOT CONCAT JMP EQ LT LE TEST CALL TAILCALL RETURN FORLOOP TFORLOOP TFORPREP SETLIST SETLISTO CLOSE CLOSURE".split()
class R:
    def __init__(s,b): s.b=b; s.o=0
    def u8(s): v=s.b[s.o]; s.o+=1; return v
    def i32(s): v=struct.unpack_from('<i',s.b,s.o)[0]; s.o+=4; return v
    def u32(s): v=struct.unpack_from('<I',s.b,s.o)[0]; s.o+=4; return v
    def num(s): v=struct.unpack_from('<f',s.b,s.o)[0]; s.o+=4; return v
    def str(s):
        n=s.u32()
        if n==0: return None
        v=s.b[s.o:s.o+n-1].decode('latin1'); s.o+=n; return v
def func(r):
    f={}
    f['src']=r.str(); f['line']=r.i32(); f['nups']=r.u8(); f['np']=r.u8(); f['va']=r.u8(); f['ms']=r.u8()
    n=r.i32(); r.o+=4*n
    n=r.i32()
    for i in range(n): r.str(); r.i32(); r.i32()
    n=r.i32(); f['ups']=[r.str() for i in range(n)]
    n=r.i32(); K=[]
    for i in range(n):
        t=r.u8()
        if t==3: K.append(r.num())
        elif t==4: K.append(r.str())
        elif t==0: K.append(None)
        else: raise Exception('const type %d'%t)
    f['K']=K
    n=r.i32(); f['P']=[func(r) for i in range(n)]
    n=r.i32(); f['code']=[r.u32() for i in range(n)]
    return f
def load(b):
    r=R(b); assert b[:4]==b'\x1bLua'; r.o=4+1+1+5+1+8  # sig,ver,endian,5 sizes(int,size_t,instr,op,a,b,c)? see below
    return func(r)
def load2(b):
    # header: ESC Lua(4) ver(1) endian(1) int size_t instr(3) OP A B C(4) number(1) testnum(8)
    r=R(b); r.o=18
    return func(r)
def fmtk(v): return repr(v) if isinstance(v,str) else ('%g'%v if isinstance(v,float) else str(v))
def decomp(f,ind=''):
    K=f['K']; reg={}; out=[]
    def RK(x): return fmtk(K[x-250]) if x>=250 else reg.get(x,'r%d'%x)
    for pc,ins in enumerate(f['code']):
        op=OPS[ins&63]; C=(ins>>6)&511; B=(ins>>15)&511; A=(ins>>24)&255; Bx=(ins>>6)&0x3FFFF; sBx=Bx-131071
        if op=='MOVE': reg[A]=reg.get(B,'r%d'%B)
        elif op=='LOADK': reg[A]=fmtk(K[Bx])
        elif op=='LOADBOOL': reg[A]=('true' if B else 'false'); 
        elif op=='LOADNIL':
            for i in range(A,B+1): reg[i]='nil'
        elif op=='GETGLOBAL': reg[A]=K[Bx]
        elif op=='GETUPVAL': reg[A]='up%d'%B
        elif op=='GETTABLE':
            k=RK(C); reg[A]=reg.get(B,'r%d'%B)+('.'+K[C-250] if C>=250 and isinstance(K[C-250],str) else '[%s]'%k)
        elif op=='SELF':
            o=reg.get(B,'r%d'%B); reg[A+1]=o; reg[A]=o+':'+(K[C-250] if C>=250 else RK(C))
        elif op=='SETGLOBAL': out.append('%d: %s = %s'%(pc,K[Bx],reg.get(A,'r%d'%A)))
        elif op=='SETTABLE': out.append('%d: %s[%s] = %s'%(pc,reg.get(A,'r%d'%A),RK(B),RK(C)))
        elif op=='NEWTABLE': reg[A]='{}'
        elif op in('ADD','SUB','MUL','DIV','POW'): reg[A]='(%s %s %s)'%(RK(B),{'ADD':'+','SUB':'-','MUL':'*','DIV':'/','POW':'^'}[op],RK(C))
        elif op=='UNM': reg[A]='-'+reg.get(B,'?')
        elif op=='NOT': reg[A]='not '+reg.get(B,'?')
        elif op=='CONCAT': reg[A]='..'.join(reg.get(i,'?') for i in range(B,C+1))
        elif op=='JMP': out.append('%d: jmp %d'%(pc,pc+1+sBx))
        elif op in ('EQ','LT','LE'): out.append('%d: if (%s %s %s) ~= %d then skip'%(pc,RK(B),{'EQ':'==','LT':'<','LE':'<='}[op],RK(C),A))
        elif op=='TEST': out.append('%d: test %s ~= %d -> skip ; r%d=%s'%(pc,reg.get(B,'r%d'%B),C,A,reg.get(B,'?')))
        elif op in('CALL','TAILCALL'):
            fn=reg.get(A,'r%d'%A); args=[]
            if fn.find(':')>=0: rng=range(A+2,A+B) if B else range(A+2,A+4)
            else: rng=range(A+1,A+B) if B else range(A+1,A+3)
            args=[reg.get(i,'r%d'%i) for i in rng]
            s='%s(%s)'%(fn,', '.join(args)); out.append('%d: %s%s'%(pc,'' if C==1 else 'r%d = '%A,s)); reg[A]=s
        elif op=='RETURN': out.append('%d: return %s'%(pc,', '.join(reg.get(i,'?') for i in range(A,A+B-1)) if B>1 else ''))
        elif op=='CLOSURE': reg[A]='function#%d'%Bx
        else: out.append('%d: %s A=%d B=%d C=%d'%(pc,op,A,B,C))
    res=[ind+l for l in out]
    for i,p in enumerate(f['P']): res.append(ind+'function#%d:'%i); res+=decomp(p,ind+'  ')
    return res

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System.Security.Cryptography;
using System.IO;
using System.Globalization;
using System.IO.Compression;
using System.Collections.Specialized;
using Confuser.Core.Poly;
using Confuser.Core.Poly.Visitors;
using Mono.Cecil.Metadata;

namespace Confuser.Core.Confusions
{
    public class CtorProxyConfusion : IConfusion
    {
        class Phase1 : StructurePhase, IProgressProvider
        {
            CtorProxyConfusion cc;
            public Phase1(CtorProxyConfusion cc) { this.cc = cc; }
            public override IConfusion Confusion
            {
                get { return cc; }
            }

            public override int PhaseID
            {
                get { return 1; }
            }

            public override Priority Priority
            {
                get { return Priority.TypeLevel; }
            }

            public override bool WholeRun
            {
                get { return false; }
            }

            ModuleDefinition mod;
            public override void Initialize(ModuleDefinition mod)
            {
                this.mod = mod;

                _Context txt = cc.txts[mod] = new _Context();

                txt.mcd = mod.Import(typeof(MulticastDelegate));
                txt.v = mod.TypeSystem.Void;
                txt.obj = mod.TypeSystem.Object;
                txt.ptr = mod.TypeSystem.IntPtr;

                txt.txts = new List<Context>();
                txt.delegates = new Dictionary<string, TypeDefinition>();
                txt.fields = new Dictionary<string, FieldDefinition>();
                txt.bridges = new Dictionary<string, MethodDefinition>();
            }
            public override void DeInitialize()
            {
                _Context txt = cc.txts[mod];

                TypeDefinition modType = mod.GetType("<Module>");
                AssemblyDefinition i = AssemblyDefinition.ReadAssembly(typeof(Iid).Assembly.Location);
                txt.proxy = i.MainModule.GetType("Proxies").Methods.FirstOrDefault(mtd => mtd.Name == "CtorProxy");
                txt.proxy = CecilHelper.Inject(mod, txt.proxy);
                modType.Methods.Add(txt.proxy);
                txt.proxy.IsAssembly = true;
                txt.proxy.Name = ObfuscationHelper.GetNewName("Proxy" + Guid.NewGuid().ToString());
                AddHelper(txt.proxy, 0);

                Instruction placeholder = null;
                txt.key = new Random().Next();
                foreach (Instruction inst in txt.proxy.Body.Instructions)
                    if (inst.Operand is MethodReference && (inst.Operand as MethodReference).Name == "PlaceHolder")
                    {
                        placeholder = inst;
                        break;
                    }
                if (txt.isNative)
                {
                    txt.nativeDecr = new MethodDefinition(
                        ObfuscationHelper.GetNewName("NativeDecrypter" + Guid.NewGuid().ToString()),
                        MethodAttributes.Abstract | MethodAttributes.CompilerControlled |
                        MethodAttributes.ReuseSlot | MethodAttributes.Static,
                        mod.TypeSystem.Int32);
                    txt.nativeDecr.ImplAttributes = MethodImplAttributes.Native;
                    txt.nativeDecr.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Int32));
                    modType.Methods.Add(txt.nativeDecr);
                    do
                    {
                        txt.exp = new ExpressionGenerator().Generate(6);
                        txt.invExp = ExpressionInverser.InverseExpression(txt.exp);
                    } while ((txt.visitor = new x86Visitor(txt.invExp, null)).RegisterOverflowed);

                    CecilHelper.Replace(txt.proxy.Body, placeholder, new Instruction[]
                        {
                            Instruction.Create(OpCodes.Call, txt.nativeDecr)
                        });
                }
                else
                    CecilHelper.Replace(txt.proxy.Body, placeholder, new Instruction[]
                        {
                            Instruction.Create(OpCodes.Ldc_I4, txt.key),
                            Instruction.Create(OpCodes.Xor)
                        });
            }

            public override void Process(ConfusionParameter parameter)
            {
                _Context txt = cc.txts[mod];
                txt.isNative = parameter.GlobalParameters["type"] == "native";
                IList<Tuple<IAnnotationProvider, NameValueCollection>> targets = parameter.Target as IList<Tuple<IAnnotationProvider, NameValueCollection>>;
                for (int i = 0; i < targets.Count; i++)
                {
                    MethodDefinition mtd = targets[i].Item1 as MethodDefinition;
                    if (!mtd.HasBody || mtd.DeclaringType.FullName == "<Module>") continue;

                    MethodBody bdy = mtd.Body;
                    foreach (Instruction inst in bdy.Instructions)
                    {
                        if (inst.OpCode.Code == Code.Newobj &&
                            !((inst.Operand as MethodReference).DeclaringType is GenericInstanceType) &&
                            !(inst.Operand is GenericInstanceMethod))
                        {
                            CreateDelegate(mtd.Body, inst, inst.Operand as MethodReference, mod);
                        }
                    }
                    progresser.SetProgress(i + 1, targets.Count);
                }
                int total = cc.txts.Count;
                int interval = 1;
                if (total > 1000)
                    interval = (int)total / 100;
                for (int i = 0; i < txt.txts.Count; i++)
                {
                    CreateFieldBridge(mod, txt.txts[i]);
                    if (i % interval == 0 || i == txt.txts.Count - 1)
                        progresser.SetProgress(i + 1, total);
                }
            }

            private void CreateDelegate(MethodBody Bdy, Instruction Inst, MethodReference MtdRef, ModuleDefinition Mod)
            {
                //Limitation
                TypeDefinition tmp = MtdRef.DeclaringType.Resolve();
                if (tmp != null && tmp.BaseType != null &&
                    (tmp.BaseType.FullName == "System.MulticastDelegate" ||
                    tmp.BaseType.FullName == "System.Delegate"))
                    return;

                _Context _txt = cc.txts[mod];

                Context txt = new Context();
                txt.inst = Inst;
                txt.bdy = Bdy;
                txt.mtdRef = MtdRef;
                string sign = GetSignatureO(MtdRef);
                if (!_txt.delegates.TryGetValue(sign, out txt.dele))
                {
                    txt.dele = new TypeDefinition("", sign, TypeAttributes.NotPublic | TypeAttributes.Sealed, _txt.mcd);
                    Mod.Types.Add(txt.dele);

                    MethodDefinition cctor = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.Static, _txt.v);
                    cctor.Body = new MethodBody(cctor);
                    txt.dele.Methods.Add(cctor);

                    MethodDefinition ctor = new MethodDefinition(".ctor", 0, _txt.v);
                    ctor.IsRuntime = true;
                    ctor.HasThis = true;
                    ctor.IsHideBySig = true;
                    ctor.IsRuntimeSpecialName = true;
                    ctor.IsSpecialName = true;
                    ctor.IsPublic = true;
                    ctor.Parameters.Add(new ParameterDefinition(_txt.obj));
                    ctor.Parameters.Add(new ParameterDefinition(_txt.ptr));
                    txt.dele.Methods.Add(ctor);

                    MethodDefinition invoke = new MethodDefinition("Invoke", 0, MtdRef.DeclaringType);
                    invoke.IsRuntime = true;
                    invoke.HasThis = true;
                    invoke.IsHideBySig = true;
                    invoke.IsVirtual = true;
                    invoke.IsPublic = true;

                    for (int i = 0; i < MtdRef.Parameters.Count; i++)
                    {
                        invoke.Parameters.Add(new ParameterDefinition(GetNameO(MtdRef.Parameters[i]), MtdRef.Parameters[i].Attributes, MtdRef.Parameters[i].ParameterType));
                    }
                    txt.dele.Methods.Add(invoke);
                    _txt.delegates.Add(sign, txt.dele);
                }
                _txt.txts.Add(txt);
            }
            private void CreateFieldBridge(ModuleDefinition Mod, Context txt)
            {
                _Context _txt = cc.txts[mod];

                ////////////////Field
                string fldId = GetId(Mod, txt.mtdRef);
                if (!_txt.fields.TryGetValue(fldId, out txt.fld))
                {
                    txt.fld = new FieldDefinition(fldId, FieldAttributes.Static | FieldAttributes.Assembly, txt.dele);
                    txt.dele.Fields.Add(txt.fld);
                    _txt.fields.Add(fldId, txt.fld);
                }
                ////////////////Bridge
                string bridgeId = GetNameO(txt.mtdRef);
                MethodDefinition bdge;
                if (!_txt.bridges.TryGetValue(bridgeId, out bdge))
                {
                    bdge = new MethodDefinition(bridgeId, MethodAttributes.Static | MethodAttributes.Assembly, txt.mtdRef.DeclaringType);
                    for (int i = 0; i < txt.mtdRef.Parameters.Count; i++)
                    {
                        bdge.Parameters.Add(new ParameterDefinition(GetNameO(txt.mtdRef.Parameters[i]), txt.mtdRef.Parameters[i].Attributes, txt.mtdRef.Parameters[i].ParameterType));
                    }
                    {
                        ILProcessor wkr = bdge.Body.GetILProcessor();
                        wkr.Emit(OpCodes.Ldsfld, txt.fld);
                        for (int i = 0; i < bdge.Parameters.Count; i++)
                        {
                            wkr.Emit(OpCodes.Ldarg, bdge.Parameters[i]);
                        }
                        wkr.Emit(OpCodes.Call, txt.dele.Methods.FirstOrDefault(mtd => mtd.Name == "Invoke"));
                        wkr.Emit(OpCodes.Ret);
                    }
                    txt.dele.Methods.Add(bdge);
                    _txt.bridges.Add(bridgeId, bdge);
                }

                ////////////////Replace
                txt.inst.OpCode = OpCodes.Call;
                txt.inst.Operand = bdge;
            }

            IProgresser progresser;
            public void SetProgresser(IProgresser progresser)
            {
                this.progresser = progresser;
            }
        }
        class Phase2 : StructurePhase, IProgressProvider
        {
            CtorProxyConfusion cc;
            public Phase2(CtorProxyConfusion cc) { this.cc = cc; }
            public override IConfusion Confusion
            {
                get { return cc; }
            }

            public override int PhaseID
            {
                get { return 2; }
            }

            public override Priority Priority
            {
                get { return Priority.TypeLevel; }
            }

            public override bool WholeRun
            {
                get { return false; }
            }

            ModuleDefinition mod;
            public override void Initialize(ModuleDefinition mod)
            {
                this.mod = mod;
            }
            public override void DeInitialize()
            {
                //
            }
            public override void Process(ConfusionParameter parameter)
            {
                _Context _txt = cc.txts[mod];

                int total = _txt.txts.Count;
                int interval = 1;
                if (total > 1000)
                    interval = (int)total / 100;
                for (int i = 0; i < _txt.txts.Count; i++)
                {
                    Context txt = _txt.txts[i];
                    txt.fld.Name = GetId(txt.mtdRef.Module, txt.mtdRef);

                    if (!(txt.fld as IAnnotationProvider).Annotations.Contains("CtorProxyCtored"))
                    {
                        ILProcessor psr = txt.dele.GetStaticConstructor().Body.GetILProcessor();
                        psr.Emit(OpCodes.Ldtoken, txt.fld);
                        psr.Emit(OpCodes.Call, _txt.proxy);
                        (txt.fld as IAnnotationProvider).Annotations["CtorProxyCtored"] = true;
                    }

                    if (i % interval == 0 || i == _txt.txts.Count - 1)
                        progresser.SetProgress(i + 1, total);
                }

                total = _txt.delegates.Count;
                interval = 1;
                if (total > 1000)
                    interval = (int)total / 100;
                IEnumerator<TypeDefinition> etor = _txt.delegates.Values.GetEnumerator();
                etor.MoveNext();
                for (int i = 0; i < _txt.delegates.Count; i++)
                {
                    etor.Current.GetStaticConstructor().Body.GetILProcessor().Emit(OpCodes.Ret);
                    etor.MoveNext();
                    if (i % interval == 0 || i == cc.txts.Count - 1)
                        progresser.SetProgress(i + 1, total);
                }
            }

            IProgresser progresser;
            public void SetProgresser(IProgresser progresser)
            {
                this.progresser = progresser;
            }
        }
        class MdPhase1 : MetadataPhase
        {
            CtorProxyConfusion cc;
            public MdPhase1(CtorProxyConfusion cc) { this.cc = cc; }
            public override IConfusion Confusion
            {
                get { return cc; }
            }

            public override int PhaseID
            {
                get { return 1; }
            }

            public override Priority Priority
            {
                get { return Priority.TypeLevel; }
            }

            public override void Process(NameValueCollection parameters, MetadataProcessor.MetadataAccessor accessor)
            {
                _Context _txt = cc.txts[accessor.Module];
                foreach (Context txt in _txt.txts)
                {
                    if (txt.fld.Name[0] != '\0') continue;
                    MetadataToken tkn = accessor.LookupToken(txt.mtdRef);
                    byte[] dat = new byte[5];
                    dat[4] = (byte)((int)tkn.TokenType >> 24);

                    if (_txt.isNative)
                        Buffer.BlockCopy(BitConverter.GetBytes(ExpressionEvaluator.Evaluate(_txt.exp, (int)tkn.RID)), 0, dat, 0, 4);
                    else
                        Buffer.BlockCopy(BitConverter.GetBytes((int)tkn.RID ^ _txt.key), 0, dat, 0, 4);

                    string str = Convert.ToBase64String(dat);
                    StringBuilder sb = new StringBuilder(str.Length);
                    for (int i = 0; i < str.Length; i++)
                        sb.Append((char)((byte)str[i] ^ i));
                    txt.fld.Name = sb.ToString();
                }
                if (!_txt.isNative) return;

                _txt.nativeRange = new Range(accessor.Codebase + (uint)accessor.Codes.Position, 0);
                MemoryStream ms = new MemoryStream();
                using (BinaryWriter wtr = new BinaryWriter(ms))
                {
                    wtr.Write(new byte[] { 0x8b, 0x44, 0x24, 0x04 });   //mov eax, [esp + 4]
                    wtr.Write(new byte[] { 0x53 });   //push ebx
                    wtr.Write(new byte[] { 0x50 });   //push eax
                    x86Visitor visitor = new x86Visitor(_txt.invExp, null);     //use default handler
                    x86Register ret;
                    var insts = _txt.visitor.GetInstructions(out ret);
                    foreach (var i in insts)
                        wtr.Write(i.Assemble());
                    if (ret != x86Register.EAX)
                        wtr.Write(
                            new x86Instruction()
                            {
                                OpCode = x86OpCode.MOV,
                                Operands = new Ix86Operand[]
                                {
                                    new x86RegisterOperand() { Register = x86Register.EAX },
                                    new x86RegisterOperand() { Register = ret }
                                }
                            }.Assemble());
                    wtr.Write(new byte[] { 0x5b });   //pop ebx
                    wtr.Write(new byte[] { 0xc3 });   //ret
                    wtr.Write(new byte[((ms.Length + 3) & ~3) - ms.Length]);
                }
                byte[] codes = ms.ToArray();
                accessor.Codes.WriteBytes(codes);
                accessor.SetCodePosition(accessor.Codebase + (uint)accessor.Codes.Position);
                _txt.nativeRange.Length = (uint)codes.Length;
            }
        }
        class MdPhase2 : MetadataPhase
        {
            CtorProxyConfusion cc;
            public MdPhase2(CtorProxyConfusion cc) { this.cc = cc; }
            public override IConfusion Confusion
            {
                get { return cc; }
            }

            public override int PhaseID
            {
                get { return 2; }
            }

            public override Priority Priority
            {
                get { return Priority.TypeLevel; }
            }

            public override void Process(NameValueCollection parameters, MetadataProcessor.MetadataAccessor accessor)
            {
                _Context txt = cc.txts[accessor.Module];
                if (!txt.isNative) return;

                var tbl = accessor.TableHeap.GetTable<MethodTable>(Table.Method);
                var row = tbl[(int)txt.nativeDecr.MetadataToken.RID - 1];
                row.Col2 = MethodImplAttributes.Native | MethodImplAttributes.Unmanaged | MethodImplAttributes.PreserveSig;
                row.Col3 &= ~MethodAttributes.Abstract;
                row.Col3 |= MethodAttributes.PInvokeImpl;
                row.Col1 = txt.nativeRange.Start;
                accessor.BodyRanges[txt.nativeDecr.MetadataToken] = txt.nativeRange;

                tbl[(int)txt.nativeDecr.MetadataToken.RID - 1] = row;

                accessor.Module.Attributes &= ~ModuleAttributes.ILOnly;
            }
        }


        public string ID
        {
            get { return "ctor proxy"; }
        }
        public string Name
        {
            get { return "Constructor Proxy Confusion"; }
        }
        public string Description
        {
            get { return "This confusion create proxies between references of constructors and methods code."; }
        }
        public Target Target
        {
            get { return Target.Methods; }
        }
        public Preset Preset
        {
            get { return Preset.Normal; }
        }
        public bool StandardCompatible
        {
            get { return true; }
        }
        public bool SupportLateAddition
        {
            get { return false; }
        }
        public Behaviour Behaviour
        {
            get { return Behaviour.Inject | Behaviour.AlterCode | Behaviour.AlterStructure; }
        }

        Phase[] ps;
        public Phase[] Phases
        {
            get
            {
                if (ps == null) ps = new Phase[] { new Phase1(this), new Phase2(this), new MdPhase1(this), new MdPhase2(this) };
                return ps;
            }
        }

        public void Init() { txts.Clear(); }
        public void Deinit() { txts.Clear(); }

        class _Context
        {
            public bool isNative;
            public Dictionary<string, TypeDefinition> delegates;
            public Dictionary<string, FieldDefinition> fields;
            public Dictionary<string, MethodDefinition> bridges;
            public MethodDefinition proxy;

            public Range nativeRange;
            public MethodDefinition nativeDecr;
            public Expression exp;
            public Expression invExp;
            public x86Visitor visitor;
            public int key;

            public List<Context> txts;
            public TypeReference mcd;
            public TypeReference v;
            public TypeReference obj;
            public TypeReference ptr;
        }
        Dictionary<ModuleDefinition, _Context> txts = new Dictionary<ModuleDefinition, _Context>();
        private class Context { public MethodBody bdy; public Instruction inst; public FieldDefinition fld; public TypeDefinition dele; public MethodReference mtdRef;}

        static string GetNameO(MethodReference mbr)
        {
            return ObfuscationHelper.GetNewName(mbr.ToString());
        }
        static string GetNameO(ParameterDefinition arg)
        {
            return ObfuscationHelper.GetNewName(arg.Name);
        }
        static string GetSignatureO(MethodReference mbr)
        {
            return ObfuscationHelper.GetNewName(GetSignature(mbr));
        }
        static string GetSignature(MethodReference mbr)
        {
            StringBuilder sig = new StringBuilder();
            sig.Append(mbr.ReturnType.FullName);
            if (mbr.Resolve() != null && mbr.Resolve().IsVirtual)
                sig.Append(" virtual");
            if (mbr.HasThis)
                sig.Append(" " + mbr.DeclaringType.ToString());
            if (mbr.Name == ".cctor" || mbr.Name == ".ctor")
                sig.Append(mbr.Name);
            sig.Append(" (");
            if (mbr.HasParameters)
            {
                for (int i = 0; i < mbr.Parameters.Count; i++)
                {
                    if (i > 0)
                    {
                        sig.Append(",");
                    }
                    sig.Append(mbr.Parameters[i].ParameterType.FullName);
                }
            }
            sig.Append(")");
            return sig.ToString();
        }

        static string GetId(ModuleDefinition mod, MethodReference mtd)
        {
            char asmRef = (char)(mod.AssemblyReferences.IndexOf(mtd.DeclaringType.Scope as AssemblyNameReference) + 2);
            return "\0" + asmRef + mtd.ToString();
        }
    }
}

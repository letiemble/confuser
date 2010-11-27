﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;

static class AntiDebugger
{
    [DllImport("ntdll.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    static extern int NtQueryInformationProcess(IntPtr ProcessHandle, int ProcessInformationClass,
        byte[] ProcessInformation, uint ProcessInformationLength, out int ReturnLength);
    [DllImport("ntdll.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    static extern uint NtSetInformationProcess(IntPtr ProcessHandle, int ProcessInformationClass,
        byte[] ProcessInformation, uint ProcessInformationLength);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);

    public static void Initialize()
    {
        System.Diagnostics.Process.EnterDebugMode();
        AntiDebug();
    }

    static void AntiDebug()
    {
        //Managed
        if (Debugger.IsAttached || Debugger.IsLogging())
            Environment.FailFast("Debugger detected (Managed)");

        if (Environment.GetEnvironmentVariable("COR_ENABLE_PROFILING") != null ||
           Environment.GetEnvironmentVariable("COR_PROFILER") != null)
            Environment.FailFast("Profiler detected");

        //Open process
        IntPtr ps = Process.GetCurrentProcess().Handle;
        if (ps == IntPtr.Zero)
            Environment.FailFast("Cannot open process");

        //PEB.BeingDebugged
        byte[] info = new byte[0x18];
        int len;
        NtQueryInformationProcess(ps, 0x0, info, 0x18, out len);
        if (len == 0)
            Environment.FailFast("Cannot query information (PEB)");

        IntPtr pebAdr;
        if (IntPtr.Size == 4)
            pebAdr = (IntPtr)BitConverter.ToInt32(info, 4);
        else
            pebAdr = (IntPtr)BitConverter.ToInt64(info, 8);

        byte[] peb = new byte[0x1d8];
        Marshal.Copy(pebAdr, peb, 0, 0x1d8);
        if (peb[2] != 0)
            Environment.FailFast("Debugger detected (PEB)");

        //DebugPort
        info = new byte[8];
        NtQueryInformationProcess(ps, 0x7, info, (uint)IntPtr.Size, out len);
        if (len != IntPtr.Size)
            Environment.FailFast("Cannot query information (Port)");

        if (BitConverter.ToInt64(info, 0) != 0)
        {
            info.Initialize();
            NtSetInformationProcess(ps, 0x7, info, (uint)IntPtr.Size);
            Environment.FailFast("Debugger detected (Port)");
        }

        //Close
        try
        {
            CloseHandle(IntPtr.Zero);
        }
        catch
        {
            Environment.FailFast("Debugger detected (Closing)");
        }

        Thread.Sleep(1000);
        Thread thread = new Thread(AntiDebug);
        thread.IsBackground = true;
        thread.Start();
    }

    private static void AntiDebugSafe()
    {
        if (Debugger.IsAttached || Debugger.IsLogging())
            Environment.FailFast("Debugger detected (Managed)");

        if (Environment.GetEnvironmentVariable("COR_ENABLE_PROFILING") != null ||
           Environment.GetEnvironmentVariable("COR_PROFILER") != null)
            Environment.FailFast("Profiler detected");

        Thread.Sleep(1000);
        Thread thread = new Thread(AntiDebugSafe);
        thread.IsBackground = true;
        thread.Start();
    }
}

static class Proxies
{
    private static void CtorProxy(RuntimeFieldHandle f)
    {
        var fld = FieldInfo.GetFieldFromHandle(f);
        var asm = Assembly.GetExecutingAssembly();
        var mtd = asm.GetModules()[0].ResolveMethod(BitConverter.ToInt32(Encoding.Unicode.GetBytes(fld.Name.ToCharArray(), 0, 2), 0) ^ 0x12345678) as System.Reflection.ConstructorInfo;

        var args = mtd.GetParameters();
        Type[] arg = new Type[args.Length];
        for (int i = 0; i < args.Length; i++)
            arg[i] = args[i].ParameterType;

        var dm = new System.Reflection.Emit.DynamicMethod("", mtd.DeclaringType, arg, mtd.DeclaringType, true);
        var gen = dm.GetILGenerator();
        for (int i = 0; i < arg.Length; i++)
            gen.Emit(System.Reflection.Emit.OpCodes.Ldarg_S, i);
        gen.Emit(System.Reflection.Emit.OpCodes.Newobj, mtd);
        gen.Emit(System.Reflection.Emit.OpCodes.Ret);

        fld.SetValue(null, dm.CreateDelegate(fld.FieldType));
    }

    private static void MtdProxy(RuntimeFieldHandle f)
    {
        var fld = System.Reflection.FieldInfo.GetFieldFromHandle(f);
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var mtd = asm.GetModules()[0].ResolveMethod(BitConverter.ToInt32(Encoding.Unicode.GetBytes(fld.Name.ToCharArray(), 1 , 2), 0) ^ 0x12345678) as System.Reflection.MethodInfo;

        if (mtd.IsStatic)
            fld.SetValue(null, Delegate.CreateDelegate(fld.FieldType, mtd));
        else
        {
            var tmp = mtd.GetParameters();
            Type[] arg = new Type[tmp.Length + 1];
            arg[0] = typeof(object);
            for (int i = 0; i < tmp.Length; i++)
                arg[i + 1] = tmp[i].ParameterType;

            System.Reflection.Emit.DynamicMethod dm;
            if (mtd.DeclaringType.IsInterface)
                dm = new System.Reflection.Emit.DynamicMethod("", mtd.ReturnType, arg, true);
            else
                dm = new System.Reflection.Emit.DynamicMethod("", mtd.ReturnType, arg, mtd.DeclaringType, true);
            var gen = dm.GetILGenerator();
            for (int i = 0; i < arg.Length; i++)
                gen.Emit(System.Reflection.Emit.OpCodes.Ldarg, i);
            gen.Emit((fld.Name[0] == '\r') ? System.Reflection.Emit.OpCodes.Callvirt : System.Reflection.Emit.OpCodes.Call, mtd);
            gen.Emit(System.Reflection.Emit.OpCodes.Ret);

            fld.SetValue(null, dm.CreateDelegate(fld.FieldType));
        }
    }
}

static class Encryptions
{
    static Assembly Resources(object sender, ResolveEventArgs args)
    {
        Assembly datAsm;
        if ((datAsm = AppDomain.CurrentDomain.GetData("PADDINGPADDINGPADDING") as System.Reflection.Assembly) == null)
        {
            Stream str = typeof(Exception).Assembly.GetManifestResourceStream("PADDINGPADDINGPADDING");
            using (BinaryReader rdr = new BinaryReader(new DeflateStream(str, CompressionMode.Decompress)))
            {
                byte[] enDat = rdr.ReadBytes(rdr.ReadInt32());
                byte[] final = new byte[enDat.Length / 2];
                for (int i = 0; i < enDat.Length; i += 2)
                {
                    final[i / 2] = (byte)((enDat[i + 1] ^ 0x11) * 0x22 + (enDat[i] ^ 0x11));
                }
                using (BinaryReader rdr1 = new BinaryReader(new DeflateStream(new MemoryStream(final), CompressionMode.Decompress)))
                {
                    byte[] fDat = rdr1.ReadBytes(rdr1.ReadInt32());
                    datAsm = System.Reflection.Assembly.Load(fDat);
                    AppDomain.CurrentDomain.SetData("PADDINGPADDINGPADDING", datAsm);
                }
            }
        }
        if (Array.IndexOf(datAsm.GetManifestResourceNames(), args.Name) == -1)
            return null;
        else
            return datAsm;
    }

    private static string SafeStrings(int id)
    {
        Dictionary<int, string> hashTbl;
        if ((hashTbl = AppDomain.CurrentDomain.GetData("PADDINGPADDINGPADDING") as Dictionary<int, string>) == null)
        {
            AppDomain.CurrentDomain.SetData("PADDINGPADDINGPADDING", hashTbl = new Dictionary<int, string>());
            MemoryStream stream = new MemoryStream();
            Assembly asm = Assembly.GetCallingAssembly();
            using (DeflateStream str = new DeflateStream(asm.GetManifestResourceStream("PADDINGPADDINGPADDING"), CompressionMode.Decompress))
            {
                byte[] dat = new byte[0x1000];
                int read = str.Read(dat, 0, 0x1000);
                do
                {
                    stream.Write(dat, 0, read);
                    read = str.Read(dat, 0, 0x1000);
                }
                while (read != 0);
            }
            AppDomain.CurrentDomain.SetData("PADDINGPADDINGPADDINGPADDING", stream.ToArray());
        }
        string ret;
        int mdTkn = new StackFrame(1).GetMethod().MetadataToken;
        int pos = (mdTkn ^ id) - 12345678;
        if (!hashTbl.TryGetValue(pos, out ret))
        {
            using (BinaryReader rdr = new BinaryReader(new MemoryStream((byte[])AppDomain.CurrentDomain.GetData("PADDINGPADDINGPADDINGPADDING"))))
            {
                rdr.BaseStream.Seek(pos, SeekOrigin.Begin);
                int len = (int)((~rdr.ReadUInt32()) ^ 87654321);
                byte[] b = rdr.ReadBytes(len);

                ///////////////////
                Random rand = new Random(88888888);

                int key = 0;
                for (int i = 0; i < b.Length; i++)
                {
                    byte o = b[i];
                    b[i] = (byte)(b[i] ^ (rand.Next() & key));
                    key += o;
                }
                hashTbl[pos] = (ret = Encoding.UTF8.GetString(b));
                ///////////////////
            }
        }
        return ret;
    }
    private static string Strings(int id)
    {
        Dictionary<int, string> hashTbl;
        if ((hashTbl = AppDomain.CurrentDomain.GetData("PADDINGPADDINGPADDING") as Dictionary<int, string>) == null)
        {
            AppDomain.CurrentDomain.SetData("PADDINGPADDINGPADDING", hashTbl = new Dictionary<int, string>());
            MemoryStream stream = new MemoryStream();
            Assembly asm = Assembly.GetCallingAssembly();
            using (DeflateStream str = new DeflateStream(asm.GetManifestResourceStream("PADDINGPADDINGPADDING"), CompressionMode.Decompress))
            {
                byte[] dat = new byte[0x1000];
                int read = str.Read(dat, 0, 0x1000);
                do
                {
                    stream.Write(dat, 0, read);
                    read = str.Read(dat, 0, 0x1000);
                }
                while (read != 0);
            }
            AppDomain.CurrentDomain.SetData("PADDINGPADDINGPADDINGPADDING", stream.ToArray());
        }
        string ret;
        int mdTkn = new StackFrame(1).GetMethod().MetadataToken;
        int pos = (mdTkn ^ id) - 12345678;
        if (!hashTbl.TryGetValue(pos, out ret))
        {
            using (BinaryReader rdr = new BinaryReader(new MemoryStream((byte[])AppDomain.CurrentDomain.GetData("PADDINGPADDINGPADDINGPADDING"))))
            {
                rdr.BaseStream.Seek(pos, SeekOrigin.Begin);
                int len = (int)((~rdr.ReadUInt32()) ^ 87654321);

                ///////////////////
                byte[] f = new byte[(len + 7) & ~7];

                for (int i = 0; i < f.Length; i++)
                {
                    Poly.PolyStart();
                    int count = 0;
                    int shift = 0;
                    byte b;
                    do
                    {
                        b = rdr.ReadByte();
                        count |= (b & 0x7F) << shift;
                        shift += 7;
                    } while ((b & 0x80) != 0);

                    f[i] = (byte)Poly.PlaceHolder((long)count);
                }

                hashTbl[pos] = (ret = Encoding.Unicode.GetString(f, 0, len));
                ///////////////////
            }
        }
        return ret;
    }
}

static class AntiTamper
{
    [DllImportAttribute("kernel32.dll")]
    static extern bool VirtualProtect(IntPtr lpAddress, uint dwSize, uint flNewProtect, out uint lpflOldProtect);

    public static unsafe void Initalize()
    {
        Module mod = typeof(AntiTamper).Module;
        IntPtr modPtr = Marshal.GetHINSTANCE(mod);
        if (modPtr == (IntPtr)(-1)) Environment.FailFast("Module error");
        Stream stream;
        bool inMem;
        if (mod.FullyQualifiedName == "<Unknown>")
        {
            inMem = true;
            stream = new UnmanagedMemoryStream((byte*)modPtr.ToPointer(), 0xfffffff, 0xfffffff, FileAccess.ReadWrite);
        }
        else
        {
            inMem = false;
            stream = new FileStream(mod.FullyQualifiedName, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        byte[] file;
        uint checkSumOffset;
        ulong checkSum;
        byte[] iv;
        byte[] dats;
        using (BinaryReader rdr = new BinaryReader(stream))
        {
            stream.Seek(0x3c, SeekOrigin.Begin);
            uint offset = rdr.ReadUInt32();
            stream.Seek(offset, SeekOrigin.Begin);
            stream.Seek(0x6, SeekOrigin.Current);
            uint sections = rdr.ReadUInt16();
            stream.Seek(offset = offset + 0x18, SeekOrigin.Begin);  //Optional hdr
            bool pe32 = (rdr.ReadUInt16() == 0x010b);
            stream.Seek(0x3e, SeekOrigin.Current);
            checkSumOffset = (uint)stream.Position;
            int len = rdr.ReadInt32() ^ 0x11111111;
            if (len == 0)
                Environment.FailFast("Broken file");

            stream.Seek(0, SeekOrigin.Begin);
            file = rdr.ReadBytes(len);
            checkSum = rdr.ReadUInt64() ^ 0x2222222222222222;
            iv = rdr.ReadBytes(rdr.ReadInt32() ^ 0x33333333);
            dats = rdr.ReadBytes(rdr.ReadInt32() ^ 0x44444444);
        }

        file[checkSumOffset] = 0;
        file[checkSumOffset + 1] = 0;
        file[checkSumOffset + 2] = 0;
        file[checkSumOffset + 3] = 0;
        byte[] md5 = MD5.Create().ComputeHash(file);
        ulong tCs = BitConverter.ToUInt64(md5, 0) ^ BitConverter.ToUInt64(md5, 8);
        if (tCs != checkSum)
            Environment.FailFast("Broken file");

        byte[] b = Decrypt(file, iv, dats);
        if (b[0] != 0xd6 || b[1] != 0x6f)
            Environment.FailFast("Broken file");
        byte[] tB = new byte[b.Length - 2];
        Buffer.BlockCopy(b, 2, tB, 0, tB.Length);
        using (BinaryReader rdr = new BinaryReader(new MemoryStream(tB)))
        {
            uint len = rdr.ReadUInt32();
            int[] codeLens = new int[len];
            IntPtr[] ptrs = new IntPtr[len];
            for (int i = 0; i < len; i++)
            {
                uint pos = rdr.ReadUInt32() ^ 0x55555555;
                if (pos == 0) continue;
                uint rva = rdr.ReadUInt32() ^ 0x55555555;
                byte[] cDat = rdr.ReadBytes(rdr.ReadInt32());
                uint old;
                IntPtr ptr = (IntPtr)((uint)modPtr + (inMem ? pos : rva));
                VirtualProtect(ptr, (uint)cDat.Length, 0x04, out old);
                Marshal.Copy(cDat, 0, ptr, cDat.Length);
                VirtualProtect(ptr, (uint)cDat.Length, old, out old);
                codeLens[i] = cDat.Length;
                ptrs[i] = ptr;
            }
            //for (int i = 0; i < len; i++)
            //{
            //    if (codeLens[i] == 0) continue;
            //    RuntimeHelpers.PrepareMethod(mod.ModuleHandle.GetRuntimeMethodHandleFromMetadataToken(0x06000000 + i + 1));
            //}
            //for (int i = 0; i < len; i++)
            //{
            //    if (codeLens[i] == 0) continue;
            //    uint old;
            //    VirtualProtect(ptrs[i], (uint)codeLens[i], 0x04, out old);
            //    Marshal.Copy(new byte[codeLens[i]], 0, ptrs[i], codeLens[i]);
            //    VirtualProtect(ptrs[i], (uint)codeLens[i], old, out old);
            //}
        }
    }

    static byte[] Decrypt(byte[] file, byte[] iv, byte[] dat)
    {
        Rijndael ri = Rijndael.Create();
        byte[] ret = new byte[dat.Length];
        MemoryStream ms = new MemoryStream(dat);
        using (CryptoStream cStr = new CryptoStream(ms, ri.CreateDecryptor(SHA256.Create().ComputeHash(file), iv), CryptoStreamMode.Read))
        { cStr.Read(ret, 0, dat.Length); }

        SHA512 sha = SHA512.Create();
        byte[] c = sha.ComputeHash(file);
        for (int i = 0; i < ret.Length; i += 64)
        {
            int len = ret.Length <= i + 64 ? ret.Length : i + 64;
            for (int j = i; j < len; j++)
                ret[j] ^= (byte)(c[j - i] ^ 0x11111111);
            c = sha.ComputeHash(ret, i, len - i);
        }
        return ret;
    }
}

static class Poly
{
    public static void PolyStart() { }
    public static double PlaceHolder(double val) { return 0; }
    public static long PlaceHolder(long val) { return 0; }
}

static class AntiDumping
{
    [DllImportAttribute("kernel32.dll")]
    static unsafe extern bool VirtualProtect(byte* lpAddress, int dwSize, uint flNewProtect, out uint lpflOldProtect);

    public static unsafe void Initalize()
    {
        uint old;
        byte* bas = (byte*)Marshal.GetHINSTANCE(typeof(AntiDumping).Module);
        byte* ptr = bas + 0x3c;
        byte* ptr2;
        ptr = ptr2 = bas + *(uint*)ptr;
        ptr += 0x6;
        ushort sectNum = *(ushort*)ptr;
        ptr = ptr2 = ptr2 + 0x18;
        bool pe32 = (*(ushort*)ptr == 0x010b);
        ptr = ptr2 = ptr2 + (pe32 ? 0xe0 : 0xf0);

        VirtualProtect(ptr - 16, 8, 0x40, out old);
        *(uint*)(ptr - 12) = 0xffffffff;
        byte* mdDir = bas + *(uint*)(ptr - 16);
        *(uint*)(ptr - 16) = 0xffffffff;
        for (int i = 0; i < sectNum; i++)
        {
            VirtualProtect(ptr, 8, 0x40, out old);
            Marshal.Copy(new byte[8], 0, (IntPtr)ptr, 8);
            ptr += 0x28;
        }
        VirtualProtect(mdDir, 0x48, 0x40, out old);
        byte* mdHdr = bas + *(uint*)(mdDir + 8);
        *(uint*)mdDir = 0xffffffff;
        *((uint*)mdDir + 1) = 0xffffffff;
        *((uint*)mdDir + 2) = 0xffffffff;
        *((uint*)mdDir + 3) = 0xffffffff;
    }
}
﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using Mono.Cecil.Cil;

namespace Confuser.Core
{
    public class PackerParameter
    {
        ModuleDefinition[] mods = null;
        byte[][] pes = null;
        NameValueCollection parameters = new NameValueCollection();

        public ModuleDefinition[] Modules { get { return mods; } internal set { mods = value; } }
        public byte[][] PEs { get { return pes; } internal set { pes = value; } }
        public NameValueCollection Parameters { get { return parameters; } internal set { parameters = value; } }
    }

    public abstract class Packer
    {
        class PackerMarker : Marker
        {
            ModuleDefinition origin;
            public PackerMarker(ModuleDefinition mod) { origin = mod; }

            public override void MarkAssembly(AssemblyDefinition asm, Preset preset, Confuser cr)
            {
                base.MarkAssembly(asm, preset, cr);

                IAnnotationProvider m = asm;
                m.Annotations.Clear();
                IAnnotationProvider src = (IAnnotationProvider)origin.Assembly;
                foreach (object key in src.Annotations.Keys)
                {
                    if (key.ToString() == "Packer" || key.ToString() == "PackerParams") continue;
                    m.Annotations[key] = src.Annotations[key];
                }
            }
            protected override void MarkModule(ModuleDefinition mod, IDictionary<IConfusion, NameValueCollection> current, Confuser cr)
            {
                IAnnotationProvider m = mod;
                m.Annotations.Clear();
                IAnnotationProvider src = (IAnnotationProvider)origin;
                foreach (object key in src.Annotations.Keys)
                    m.Annotations.Add(key, src.Annotations[src]);

                var dict = (IDictionary<IConfusion, NameValueCollection>)src.Annotations["ConfusionSets"];
                current.Clear();
                foreach (var i in dict)
                    current.Add(i.Key, i.Value);
            }
        }

        public abstract string ID { get; }
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract bool StandardCompatible { get; }
        Confuser cr;
        internal Confuser Confuser { get { return cr; } set { cr = value; } }
        protected void Log(string message) { cr.Log(message); }

        public byte[] Pack(ConfuserParameter crParam, PackerParameter param)
        {
            AssemblyDefinition asm;
            PackCore(out asm, param);

            string tmp = Path.GetTempPath() + "\\" + Path.GetRandomFileName() + "\\";
            Directory.CreateDirectory(tmp);
            asm.Write(tmp + asm.MainModule.Name);

            Confuser cr = new Confuser();
            ConfuserParameter par = new ConfuserParameter();
            par.SourceAssembly = tmp + asm.MainModule.Name;
            par.ReferencesPath = tmp;
            tmp = Path.GetTempPath() + "\\" + Path.GetRandomFileName() + "\\";
            par.DestinationPath = tmp;
            par.Confusions = crParam.Confusions;
            par.DefaultPreset = crParam.DefaultPreset;
            par.StrongNameKeyPath = crParam.StrongNameKeyPath;
            par.Marker = new PackerMarker(param.Modules[0]);
            cr.Confuse(par);

            return File.ReadAllBytes(tmp + asm.MainModule.Name);
        }
        protected abstract void PackCore(out AssemblyDefinition asm, PackerParameter parameter);
    }
}
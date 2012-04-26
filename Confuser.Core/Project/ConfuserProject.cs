﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Collections.Specialized;

namespace Confuser.Core.Project
{
    public class SettingItem<T> : NameValueCollection
    {
        public string Id { get; set; }

        public XmlElement Save(XmlDocument xmlDoc)
        {
            XmlElement elem = xmlDoc.CreateElement(typeof(T) == typeof(Packer) ? "packer" : "confusion");

            XmlAttribute idAttr = xmlDoc.CreateAttribute("id");
            idAttr.Value = Id;
            elem.Attributes.Append(idAttr);

            foreach (var i in this.AllKeys)
            {
                XmlElement arg = xmlDoc.CreateElement("argument");

                XmlAttribute nameAttr = xmlDoc.CreateAttribute("name");
                nameAttr.Value = i;
                arg.Attributes.Append(nameAttr);
                XmlAttribute valAttr = xmlDoc.CreateAttribute("value");
                valAttr.Value = base[i];
                arg.Attributes.Append(valAttr);

                elem.AppendChild(arg);
            }

            return elem;
        }

        public void Load(XmlElement elem)
        {
            this.Id = elem.Attributes["id"].Value;
            foreach (XmlElement i in elem.ChildNodes)
                this.Add(i.Attributes["name"].Value, i.Attributes["value"].Value);
        }
    }
    public class ObfSettings : List<SettingItem<IConfusion>>
    {
        public string Name { get; set; }

        public XmlElement Save(XmlDocument xmlDoc)
        {
            XmlElement elem = xmlDoc.CreateElement("settings");

            XmlAttribute attr = xmlDoc.CreateAttribute("name");
            attr.Value = Name;
            elem.Attributes.Append(attr);

            foreach (var i in this)
                elem.AppendChild(i.Save(xmlDoc));

            return elem;
        }

        public void Load(XmlElement elem)
        {
            this.Name = elem.Attributes["name"].Value;
            foreach (XmlElement i in elem.ChildNodes)
            {
                var x = new SettingItem<IConfusion>();
                x.Load(i);
                this.Add(x);
            }
        }
    }
    public class ObfConfig
    {
        public string Id { get; set; }
        public bool ApplyToMembers { get; set; }

        public XmlElement Save(XmlDocument xmlDoc)
        {
            XmlElement elem = xmlDoc.CreateElement("config");

            XmlAttribute idAttr = xmlDoc.CreateAttribute("id");
            idAttr.Value = Id;
            elem.Attributes.Append(idAttr);

            if (ApplyToMembers != false)
            {
                XmlAttribute attr = xmlDoc.CreateAttribute("applytomember");
                attr.Value = ApplyToMembers.ToString().ToLower();
                elem.Attributes.Append(attr);
            }

            return elem;
        }

        public void Load(XmlElement elem)
        {
            this.Id = elem.Attributes["id"].Value;
            if (elem.Attributes["applytomember"] != null)
                this.ApplyToMembers = bool.Parse(elem.Attributes["applytomember"].Value);
        }
    }

    public class ConfuserProject : List<ProjectAssembly>
    {
        public ConfuserProject()
        {
            Plugins = new List<string>();
            Settings = new List<ObfSettings>();
        }
        public IList<string> Plugins { get; private set; }
        public IList<ObfSettings> Settings { get; private set; }
        public string OutputPath { get; set; }
        public string SNKeyPath { get; set; }
        public Preset DefaultPreset { get; set; }
        public SettingItem<Packer> Packer { get; set; }

        public XmlDocument Save()
        {
            XmlDocument xmlDoc = new XmlDocument();

            XmlElement elem = xmlDoc.CreateElement("project");

            XmlAttribute outputAttr = xmlDoc.CreateAttribute("outputDir");
            outputAttr.Value = OutputPath;
            elem.Attributes.Append(outputAttr);

            XmlAttribute snAttr = xmlDoc.CreateAttribute("snKey");
            snAttr.Value = SNKeyPath;
            elem.Attributes.Append(snAttr);

            if (DefaultPreset != Preset.None)
            {
                XmlAttribute presetAttr = xmlDoc.CreateAttribute("preset");
                presetAttr.Value = DefaultPreset.ToString().ToLower();
                elem.Attributes.Append(presetAttr);
            }

            foreach (var i in Plugins)
            {
                XmlElement plug = xmlDoc.CreateElement("plugin");

                XmlAttribute pathAttr = xmlDoc.CreateAttribute("path");
                pathAttr.Value = i;
                plug.Attributes.Append(pathAttr);

                elem.AppendChild(plug);
            }

            foreach (var i in Settings)
                elem.AppendChild(i.Save(xmlDoc));

            if (Packer != null)
                elem.AppendChild(Packer.Save(xmlDoc));

            foreach (var i in this)
                elem.AppendChild(i.Save(xmlDoc));

            xmlDoc.AppendChild(elem);
            return xmlDoc;
        }

        public void Load(XmlDocument doc)
        {
            XmlElement docElem = doc.DocumentElement;

            this.OutputPath = docElem.Attributes["outputDir"].Value;
            this.SNKeyPath = docElem.Attributes["snKey"].Value;
            if (docElem["preset"] != null)
                this.DefaultPreset = (Preset)Enum.Parse(typeof(Preset), docElem.Attributes["preset"].Value, true);
            foreach (XmlElement i in docElem.ChildNodes)
            {
                if (i.Name == "plugin")
                {
                    Plugins.Add(i.Attributes["path"].Value);
                }
                else if (i.Name == "settings")
                {
                    ObfSettings settings = new ObfSettings();
                    settings.Load(i);
                    Settings.Add(settings);
                }
                else if (i.Name == "packer")
                {
                    Packer = new SettingItem<Packer>();
                    Packer.Load(i);
                }
                else
                {
                    ProjectAssembly asm = new ProjectAssembly();
                    asm.Load(i);
                    this.Add(asm);
                }
            }
        }
    }
}

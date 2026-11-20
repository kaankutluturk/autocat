using System;
using System.Reflection;

[assembly: AssemblyTitle("AutoCat game component")]
[assembly: AssemblyDescription("Game-side component of AutoCat, the Bongo Cat automation helper")]
[assembly: AssemblyProduct("AutoCat")]
[assembly: AssemblyCompany("AutoCat")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Kaan Kutluturk")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace System.Reflection {
    [AttributeUsage(AttributeTargets.Assembly)]
    sealed class AssemblyTitleAttribute : Attribute {
        public AssemblyTitleAttribute(string text) { }
    }

    [AttributeUsage(AttributeTargets.Assembly)]
    sealed class AssemblyDescriptionAttribute : Attribute {
        public AssemblyDescriptionAttribute(string text) { }
    }

    [AttributeUsage(AttributeTargets.Assembly)]
    sealed class AssemblyCompanyAttribute : Attribute {
        public AssemblyCompanyAttribute(string text) { }
    }

    [AttributeUsage(AttributeTargets.Assembly)]
    sealed class AssemblyCopyrightAttribute : Attribute {
        public AssemblyCopyrightAttribute(string text) { }
    }

    [AttributeUsage(AttributeTargets.Assembly)]
    sealed class AssemblyFileVersionAttribute : Attribute {
        public AssemblyFileVersionAttribute(string text) { }
    }
}

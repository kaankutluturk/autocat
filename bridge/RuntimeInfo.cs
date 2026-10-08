using System;
using System.Reflection;

// There is deliberately no AssemblyVersion: the runtime is identified by its file name, and the loader opens it by path.
[assembly: AssemblyTitle("AutoCat game component")]
[assembly: AssemblyDescription("Game-side component of AutoCat, the Bongo Cat automation helper")]
[assembly: AssemblyProduct("AutoCat")]
[assembly: AssemblyCompany("AutoCat")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Kaan Kutluturk")]
[assembly: AssemblyFileVersion("1.2.0.0")]

// The game's trimmed mscorlib does not define these. The compiler writes the file's version resource from them by name.
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

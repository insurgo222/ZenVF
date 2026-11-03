using System.Runtime.CompilerServices;
using System.Windows;

// Unit tests exercise internal helpers (model parsing, voltage-source choice,
// thermal guard) without hardware.
[assembly: InternalsVisibleTo("ZenVF.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

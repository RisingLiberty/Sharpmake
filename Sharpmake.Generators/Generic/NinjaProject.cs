using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.Reflection;
using LibGit2Sharp;
using System.Runtime.Serialization;

namespace Sharpmake.Generators.Generic
{
    // A ninja project is the representation of a project
    // That uses ninja files to build itself.
    // A ninja project is a json file listing the project name
    // and its config, which then refer to a ninja file to build the project in that config.
    public partial class NinjaProject : IProjectGenerator
    {
        // The generation context is a class holding various settings on how to generate the project
        // It holds the build, project path, project, compiler, configuartion, .. in it, making it
        // easier to access these settings when performing the generation code
        private class GenerationContext : IGenerationContext
        {
            private Dictionary<Project.Configuration, Options.ExplicitOptions> _projectConfigurationOptions;
            private IDictionary<string, string> _cmdLineOptions;
            private IDictionary<string, string> _linkerCmdLineOptions;
            private Resolver _envVarResolver;

            public Builder Builder { get; }
            public string ProjectPath { get; }
            public string ProjectDirectory { get; }
            public string ProjectFileName { get; }
            public string ProjectDirectoryCapitalized { get; }
            public string ProjectSourceCapitalized { get; }
            public bool PlainOutput { get { return true; } }
            public Project Project { get; }
            public Compiler Compiler { get; }

            public Project.Configuration Configuration { get; set; }

            public IReadOnlyDictionary<Project.Configuration, Options.ExplicitOptions> ProjectConfigurationOptions => _projectConfigurationOptions;

            public void SetProjectConfigurationOptions(Dictionary<Project.Configuration, Options.ExplicitOptions> projectConfigurationOptions)
            {
                _projectConfigurationOptions = projectConfigurationOptions;
            }

            public DevEnv DevelopmentEnvironment => Configuration.Target.GetFragment<DevEnv>();
            public DevEnvRange DevelopmentEnvironmentsRange { get; }
            public Options.ExplicitOptions Options
            {
                get
                {
                    Debug.Assert(_projectConfigurationOptions.ContainsKey(Configuration));
                    return _projectConfigurationOptions[Configuration];
                }
            }
            public IDictionary<string, string> CommandLineOptions
            {
                get
                {
                    Debug.Assert(_cmdLineOptions != null);
                    return _cmdLineOptions;
                }
                set
                {
                    _cmdLineOptions = value;
                }
            }
            public IDictionary<string, string> LinkerCommandLineOptions
            {
                get
                {
                    Debug.Assert(_linkerCmdLineOptions != null);
                    return _linkerCmdLineOptions;
                }
                set
                {
                    _linkerCmdLineOptions = value;
                }
            }
            public Resolver EnvironmentVariableResolver
            {
                get
                {
                    Debug.Assert(_envVarResolver != null);
                    return _envVarResolver;
                }
                set
                {
                    _envVarResolver = value;
                }
            }

            public FastBuildMakeCommandGenerator FastBuildMakeCommandGenerator { get; }

            public GenerationContext(Builder builder, string projectPath, Project project, Project.Configuration configuration)
            {
                Builder = builder;

                FileInfo fileInfo = new FileInfo(projectPath);
                ProjectPath = fileInfo.FullName;
                ProjectDirectory = Path.GetDirectoryName(ProjectPath);
                ProjectFileName = Path.GetFileName(ProjectPath);
                Project = project;

                ProjectDirectoryCapitalized = Util.GetCapitalizedPath(ProjectDirectory);
                ProjectSourceCapitalized = Util.GetCapitalizedPath(Project.SourceRootPath);

                Configuration = configuration;
                Compiler = configuration.Target.GetFragment<Compiler>();
            }

            public void Reset()
            {
                CommandLineOptions = null;
                Configuration = null;
                EnvironmentVariableResolver = null;
            }

            public void SelectOption(params Options.OptionAction[] options)
            {
                Sharpmake.Options.SelectOption(Configuration, options);
            }

            public void SelectOptionWithFallback(Action fallbackAction, params Options.OptionAction[] options)
            {
                Sharpmake.Options.SelectOptionWithFallback(Configuration, fallbackAction, options);
            }

        }

        // A compile statement is a build statement to compile a C or C++ file.
        private class CompileStatement
        {
            private string Input;
            private string Output;
            private GenerationContext Context;
            private static List<string> DebugCompilerFlags = new List<string>();

            // Defines needed on the commandline for this compile statement
            public Strings Defines { get; set; }
            // The path where dependencies will be written to
            public string DepPath { get; set; }
            public Strings ImplicitCompilerFlags { get; set; }
            public Strings CompilerFlags { get; set; }
            public OrderableStrings Includes { get; set; }
            public OrderableStrings SystemIncludes { get; set; }
            public string TargetFilePath { get; set; }

            public CompileStatement(GenerationContext context, string input, string output)
            {
                Context = context;
                Input = input;
                Output = output;

                Defines = context.Configuration.Defines;
                DepPath = ConvertToNinjaFilePath(output);
                ImplicitCompilerFlags = GetImplicitCompilerFlags(context, output);
                CompilerFlags = GetCompilerFlags(context);

                OrderableStrings includePaths = context.Configuration.IncludePaths;
                includePaths.AddRange(context.Configuration.IncludePrivatePaths);
                includePaths.AddRange(context.Configuration.DependenciesIncludePaths);
                Includes = includePaths;
                OrderableStrings systemIncludePaths = context.Configuration.IncludeSystemPaths;
                systemIncludePaths.AddRange(context.Configuration.DependenciesIncludeSystemPaths);
                SystemIncludes = systemIncludePaths;
                TargetFilePath = context.Configuration.CompilerPdbFilePath;
            }

            public override string ToString()
            {
                var fileGenerator = new FileGenerator();

                string defines = MergeMultipleFlagsToString(Defines, false, CompilerFlagLookupTable.Get(Context.Compiler, CompilerFlag.Define));
                string implicitCompilerFlags = MergeMultipleFlagsToString(ImplicitCompilerFlags);
                string compilerFlags = MergeMultipleFlagsToString(CompilerFlags);
                string includes = MergeMultipleFlagsToString(Includes, true, CompilerFlagLookupTable.Get(Context.Compiler, CompilerFlag.Include));
                string systemIncludes = MergeMultipleFlagsToString(SystemIncludes, true, CompilerFlagLookupTable.Get(Context.Compiler, CompilerFlag.SystemInclude));

                bool isCompileAsCFile = Context.Configuration.ResolvedSourceFilesWithCompileAsCOption.Contains(Output);
                string compilerStatement = isCompileAsCFile 
                    ? Template.RuleStatement.CompileCFile(Context) 
                    : Template.RuleStatement.CompileCppFile(Context);

                // Some compiler flags need to be changed if we're calling into the C compiler
                if (isCompileAsCFile)
                {
                    compilerFlags = ChangeCppStandardToCStandard(compilerFlags);
                }

                fileGenerator.WriteLine($"{Template.BuildBegin}{ConvertToNinjaFilePath(Output)}: {compilerStatement} {ConvertToNinjaFilePath(Input)}");

                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.Defines(Context)}", defines);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.DepFile(Context)}", $"{DepPath}.d");
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.CompilerImplicitFlags(Context)}", implicitCompilerFlags);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.SystemIncludes(Context)}", systemIncludes);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.CompilerFlags(Context)}", compilerFlags);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.Includes(Context)}", includes);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.TargetPdb(Context)}", TargetFilePath);

                return fileGenerator.ToString();
            }
            private string ChangeCppStandardToCStandard(string compilerFlags)
            {
                string languageStandard = "";
                string ClanguageStandard = "";

                if (Context.Compiler == Compiler.MSVC)
                {
                    Context.CommandLineOptions.TryGetValue("LanguageStandard", out languageStandard);
                    Context.CommandLineOptions.TryGetValue("LanguageStandard_C", out ClanguageStandard);
                }
                else // For clang
                {
                    Context.CommandLineOptions.TryGetValue("CppLanguageStd", out languageStandard);
                    Context.CommandLineOptions.TryGetValue("CLanguageStd", out ClanguageStandard);
                }

                if (ClanguageStandard == FileGeneratorUtilities.RemoveLineTag)
                {
                    ClanguageStandard = "";
                }

                return compilerFlags.Replace(languageStandard, ClanguageStandard);
            }

            // subtract all compiler options from the config and translate them to compiler specific flags
            private Strings GetImplicitCompilerFlags(GenerationContext context, string ninjaObjPath)
            {
                Strings flags = new Strings();
                switch (context.Configuration.Target.GetFragment<Compiler>())
                {
                    case Compiler.MSVC:
                        flags.Add("/showIncludes"); // used to generate header dependencies
                        flags.Add("/nologo"); // supress copyright banner in compiler
                        flags.Add("/TP"); // treat all files on command line as C++ files
                        flags.Add(" /c"); // don't auto link
                        flags.Add($" /Fo\"{ninjaObjPath}\""); // obj output path
                        flags.Add($" /FS"); // force async pdb generation
                        break;
                    case Compiler.Clang:
                        flags.Add(" -MD"); // generate header dependencies
                        flags.Add(" -MF"); // write the header dependencies to a file
                        flags.Add($" {ninjaObjPath}.d"); // file to write header dependencies to
                        flags.Add(" -c"); // don't auto link
                        flags.Add($" -o\"{ninjaObjPath}\""); // obj output path
                        if (context.Configuration.NinjaGenerateCodeCoverage)
                        {
                            flags.Add("-fprofile-instr-generate");
                            flags.Add("-fcoverage-mapping");
                        }
                        if (context.Configuration.NinjaEnableAddressSanitizer)
                        {
                            flags.Add("-fsanitize=address");
                            context.CommandLineOptions["Optimization"] = "-O1"; // override optimization option to have stack frames

                            // disable lto to avoid asan odr issues.
                            // can't disable them with ASAN_OPTIONS=detect_odr_violation=0 due to unknown bug
                            // https://github.com/google/sanitizers/issues/647
                            context.CommandLineOptions["CompilerWholeProgramOptimization"] = FileGeneratorUtilities.RemoveLineTag;
                            context.LinkerCommandLineOptions["LinkTimeCodeGeneration"] = FileGeneratorUtilities.RemoveLineTag;
                        }
                        if (context.Configuration.NinjaEnableUndefinedBehaviorSanitizer)
                        {
                            flags.Add("-fsanitize=undefined");
                            context.CommandLineOptions["Optimization"] = "-O1"; // override optimization option to have stack frames
                        }
                        if (context.Configuration.NinjaEnableFuzzyTesting)
                        {
                            flags.Add("-fsanitize=fuzzer");
                            context.CommandLineOptions["Optimization"] = "-O1"; // override optimization option to have stack frames
                        }
                        break;
                    case Compiler.GCC:
                        flags.Add(" -D_M_X64"); // used in corecrt_stdio_config.h
                        flags.Add($" -o\"{ninjaObjPath}\""); // obj output path
                        flags.Add(" -c"); // don't auto link
                        break;
                    default:
                        throw new Error("Unknown Compiler used for implicit compiler flags");
                }

                if (context.Configuration.Output == Project.Configuration.OutputType.Dll)
                {
                    if (PlatformRegistry.Get<IPlatformDescriptor>(context.Configuration.Platform).HasSharedLibrarySupport)
                    {
                        flags.Add($" {CompilerFlagLookupTable.Get(context.Compiler, CompilerFlag.Define)}_WINDLL");
                    }
                }

                return flags;
            }

            private Strings GetCompilerFlags(GenerationContext context)
            {
                // If the file is modified, we don't want to use optimization
                // As the user will likely want to debug this file
                if (IsFileModifiedFromGit(Input))
                {
                    if (DebugCompilerFlags.Count == 0)
                    {
                        // Disable the optimisation settings
                        context.Configuration.Options.Add(Options.Vc.Compiler.Intrinsic.Disable);
                        context.Configuration.Options.Add(Options.Vc.Compiler.Inline.Default);
                        context.Configuration.Options.Add(Options.Vc.Compiler.FavorSizeOrSpeed.Neither);
                        context.Configuration.Options.Add(Options.Vc.Compiler.Optimization.Disable);

                        // Save the old commandline options
                        var oldCommandLineOptions = context.CommandLineOptions;
                        var oldLinkerCommandLineOptions = context.LinkerCommandLineOptions;

                        // Create the commandline optoins
                        GenerateConfOptions(context);

                        // Save the new, debug commandline options
                        DebugCompilerFlags = new List<string>(context.CommandLineOptions.Values);

                        // Remove the optimisation settings
                        context.Configuration.Options.Remove(Options.Vc.Compiler.Intrinsic.Disable);
                        context.Configuration.Options.Remove(Options.Vc.Compiler.Inline.Default);
                        context.Configuration.Options.Remove(Options.Vc.Compiler.FavorSizeOrSpeed.Neither);
                        context.Configuration.Options.Remove(Options.Vc.Compiler.Optimization.Disable);

                        // Put back the original commandline options
                        context.CommandLineOptions = oldCommandLineOptions;
                        context.LinkerCommandLineOptions = oldLinkerCommandLineOptions;
                    }

                    return new Strings(DebugCompilerFlags);
                }
                else
                {
                    return new Strings(context.CommandLineOptions.Values);
                }
            }
        }

        // A link statement is a build statement to link a exe or dll files
        // Link statements take previously build obj files as input.
        // These obj files are generated from compile statements
        private class LinkStatement
        {
            public string ResponseFilePath { get; set; }
            public Strings ImplicitLinkerFlags { get; set; }
            public Strings Flags { get; set; }
            public Strings ImplicitLinkerPaths { get; set; }
            public Strings ImplicitLinkerLibs { get; set; }
            public Strings LinkerPaths { get; set; }
            public Strings LinkerLibs { get; set; }
            public string PreBuild { get; set; }
            public string PostBuild { get; set; }
            public string TargetPdb { get; set; }

            private GenerationContext Context;
            private string Output; // the filepath of the targeted output
            private Strings Input; // the list of obj files used as input

            public LinkStatement(GenerationContext context, string output, Strings input)
            {
                Context = context;
                Output = output;
                Input = input;

                ResponseFilePath = CreateLinkerResponseFile(context, input);
                ImplicitLinkerFlags = GetImplicitLinkerFlags(context, output);
                Flags = GetLinkerFlags(context);
                ImplicitLinkerPaths = GetImplicitLinkPaths(context);
                ImplicitLinkerLibs = GetImplicitLinkLibraries(context);
                LinkerPaths = GetLinkerPaths(context);
                TargetPdb = context.Configuration.LinkerPdbFilePath;

                if (context.Configuration.Output != Project.Configuration.OutputType.Lib)
                {
                    LinkerPaths.AddRange(context.Configuration.DependenciesLibraryPaths);
                }
                LinkerLibs = GetLinkLibraries(context);
                if (context.Configuration.Output != Project.Configuration.OutputType.Lib)
                {
                    LinkerLibs.AddRange(ConvertLibraryDependencyFiles(context));
                }

                PreBuild = GetPreBuildCommands(context);
                PostBuild = GetPostBuildCommands(context);
            }

            public override string ToString()
            {
                var fileGenerator = new FileGenerator();

                string implicitLinkerFlags = MergeMultipleFlagsToString(ImplicitLinkerFlags);
                string linkerFlags = MergeMultipleFlagsToString(Flags);

                string implicitLinkerPaths = "";
                string implicitLinkerLibs = "";
                string libraryPaths = "";
                string libraryFiles = "";

                // when creating an exe or dll, we can use additional linker libs, if we're creating a static lib that depends on another however, we can't do this
                // and have to add the archive as an additional input file
                if (Context.Configuration.Output != Project.Configuration.OutputType.Lib)
                {
                    implicitLinkerPaths = MergeMultipleFlagsToString(ImplicitLinkerPaths, true, LinkerFlagLookupTable.Get(Context.Compiler, LinkerFlag.IncludePath));
                    implicitLinkerLibs = MergeMultipleFlagsToString(ImplicitLinkerLibs, false, LinkerFlagLookupTable.Get(Context.Compiler, LinkerFlag.IncludeFile));
                    libraryPaths = MergeMultipleFlagsToString(LinkerPaths, true, LinkerFlagLookupTable.Get(Context.Compiler, LinkerFlag.IncludePath));
                    libraryFiles = MergeMultipleFlagsToString(LinkerLibs, false, LinkerFlagLookupTable.Get(Context.Compiler, LinkerFlag.IncludeFile));
                }

                fileGenerator.Write($"{Template.BuildBegin}{FullNinjaTargetPath(Context)}: {Template.RuleStatement.LinkToUse(Context)}");
                fileGenerator.Write(" | ");
                
                foreach (string objPath in Input)
                {
                    fileGenerator.Write($" {ConvertToNinjaFilePath(objPath)}");
                }

                fileGenerator.WriteLine("");

                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.LinkerResponseFile(Context)}", $"@{ConvertToNinjaFilePath(ResponseFilePath)}");
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.ImplicitLinkerFlags(Context)}", implicitLinkerFlags);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.ImplicitLinkerPaths(Context)}", implicitLinkerPaths);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.ImplicitLinkerLibraries(Context)}", implicitLinkerLibs);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.LinkerFlags(Context)}", linkerFlags);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.LinkerPaths(Context)}", libraryPaths);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.LinkerLibraries(Context)}", libraryFiles);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.TargetFile(Context)}", Output);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.TargetPdb(Context)}", TargetPdb);
                WriteIfNotEmptyOr(fileGenerator, $"  {Template.BuildStatement.PreBuild(Context)}", PreBuild, "cd .");
                WriteIfNotEmptyOr(fileGenerator, $"  {Template.BuildStatement.PostBuild(Context)}", PostBuild, "cd .");

                return fileGenerator.ToString();
            }

            private string CreateLinkerResponseFile(GenerationContext context, Strings inputFiles)
            {
                string fullFileName = $"{context.Configuration.TargetFileFullName}_{context.Configuration.Target.ProjectConfigurationName}_{context.Compiler}_linker_response.txt";
                string responseFilePath = Path.Combine(context.Configuration.IntermediatePath, fullFileName);

                StringBuilder sb = new StringBuilder();
                foreach (string file in inputFiles)
                {
                    sb.Append($"{file.Replace('\\', '/')} ");
                }

                File.WriteAllText(responseFilePath, sb.ToString());
                return responseFilePath;
            }

            private Strings GetImplicitLinkerFlags(GenerationContext context, string outputPath)
            {
                Strings flags = new Strings();
                switch (context.Configuration.Target.GetFragment<Compiler>())
                {
                    case Compiler.MSVC:
                        flags.Add($" /OUT:{outputPath}"); // Output file
                        if (context.Configuration.Output == Project.Configuration.OutputType.Dll)
                        {
                            flags.Add(" /dll");
                        }
                        break;
                    case Compiler.Clang:
                        if (context.Configuration.Output != Project.Configuration.OutputType.Lib)
                        {
                            flags.Add(" -fuse-ld=lld-link"); // use the llvm lld linker
                            flags.Add(" -nostartfiles"); // Do not use the standard system startup files when linking
                            flags.Add(" -nostdlib"); // Do not use the standard system startup files or libraries when linking
                            flags.Add($" -o {outputPath}"); // Output file
                            if (context.Configuration.Output == Project.Configuration.OutputType.Dll)
                            {
                                flags.Add(" -shared");
                            }
                            if (context.Configuration.NinjaGenerateCodeCoverage)
                            {
                                flags.Add("-fprofile-instr-generate");
                            }
                            if (context.Configuration.NinjaEnableAddressSanitizer)
                            {
                                flags.Add("-fsanitize=address");
                            }
                            if (context.Configuration.NinjaEnableUndefinedBehaviorSanitizer)
                            {
                                flags.Add("-fsanitize=undefined");
                            }
                            if (context.Configuration.NinjaEnableFuzzyTesting)
                            {
                                flags.Add("-fsanitize=fuzzer");
                            }
                        }
                        else
                        {
                            flags.Add(" qc");
                            flags.Add($" {outputPath}"); // Output file
                        }
                        break;
                    case Compiler.GCC:
                        //flags += " -fuse-ld=lld"; // use the llvm lld linker
                        //flags.Add(" -nostdlib"); // Do not use the standard system startup files or libraries when linking
                        if (context.Configuration.Output != Project.Configuration.OutputType.Exe)
                        {
                            flags.Add($"qc {outputPath}"); // Output file
                        }
                        else
                        {
                            flags.Add($"-o {outputPath}"); // Output file
                        }
                        if (context.Configuration.Output == Project.Configuration.OutputType.Dll)
                        {
                            flags.Add(" -shared");
                        }
                        break;
                    default:
                        throw new Error("Unknown Compiler used for implicit linker flags");
                }

                return flags;
            }

            private Strings GetImplicitLinkPaths(GenerationContext context)
            {
                Strings linkPath = new Strings();

                if (context.Configuration.Output != Project.Configuration.OutputType.Lib)
                {

                    switch (context.Configuration.Target.GetFragment<Compiler>())
                    {
                        case Compiler.MSVC:
                            linkPath.Add("D:/Tools/MSVC/install/14.29.30133/lib/x64");
                            linkPath.Add("D:/Tools/MSVC/install/14.29.30133/atlmfc/lib/x64");
                            linkPath.Add("D:/Tools/Windows SDK/10.0.19041.0/lib/ucrt/x64");
                            linkPath.Add("D:/Tools/Windows SDK/10.0.19041.0/lib/um/x64");
                            break;
                        case Compiler.Clang:
                            break;
                        case Compiler.GCC:
                            break;
                    }
                }
                return linkPath;
            }

            private Strings GetImplicitLinkLibraries(GenerationContext context)
            {
                Strings linkLibraries = new Strings();

                if (context.Configuration.Output == Project.Configuration.OutputType.Lib)
                    return linkLibraries;

                switch (context.Configuration.Target.GetFragment<Compiler>())
                {
                    case Compiler.MSVC:
                        linkLibraries.Add("kernel32.lib");
                        linkLibraries.Add("user32.lib");
                        linkLibraries.Add("gdi32.lib");
                        linkLibraries.Add("winspool.lib");
                        linkLibraries.Add("shell32.lib");
                        linkLibraries.Add("ole32.lib");
                        linkLibraries.Add("oleaut32.lib");
                        linkLibraries.Add("uuid.lib");
                        linkLibraries.Add("comdlg32.lib");
                        linkLibraries.Add("advapi32.lib");
                        linkLibraries.Add("oldnames.lib");
                        break;
                    case Compiler.Clang:
                        linkLibraries.Add("kernel32");
                        linkLibraries.Add("user32");
                        linkLibraries.Add("gdi32");
                        linkLibraries.Add("winspool");
                        linkLibraries.Add("shell32");
                        linkLibraries.Add("ole32");
                        linkLibraries.Add("oleaut32");
                        linkLibraries.Add("uuid");
                        linkLibraries.Add("comdlg32");
                        linkLibraries.Add("advapi32");
                        linkLibraries.Add("oldnames");
                        linkLibraries.Add("libcmt.lib");
                        break;
                    case Compiler.GCC:
                        //linkLibraries.Add("kernel32");
                        //linkLibraries.Add("user32");
                        //linkLibraries.Add("gdi32");
                        //linkLibraries.Add("winspool");
                        //linkLibraries.Add("shell32");
                        //linkLibraries.Add("ole32");
                        //linkLibraries.Add("oleaut32");
                        //linkLibraries.Add("uuid");
                        //linkLibraries.Add("comdlg32");
                        //linkLibraries.Add("advapi32");
                        //linkLibraries.Add("oldnames");
                        break;
                }

                return linkLibraries;
            }

            private Strings GetLinkerPaths(GenerationContext context)
            {
                return new Strings(context.Configuration.LibraryPaths);
            }
            private Strings GetLinkLibraries(GenerationContext context)
            {
                return new Strings(context.Configuration.LibraryFiles);
            }

            private Strings GetLinkerFlags(GenerationContext context)
            {
                Strings flags = new Strings(context.LinkerCommandLineOptions.Values);

                // If we're making an archive, not all linker flags are supported
                switch (context.Compiler)
                {
                    case Compiler.MSVC:
                        return FilterMsvcLinkerFlags(flags, context);
                    case Compiler.Clang:
                        return FilterClangLinkerFlags(flags, context);
                    case Compiler.GCC:
                        return FilterGccLinkerFlags(flags, context);
                    default:
                        throw new Error($"Not linker flag filtering implemented for compiler {context.Compiler}");
                }
            }

            private Strings FilterMsvcLinkerFlags(Strings flags, GenerationContext context)
            {
                switch (context.Configuration.Output)
                {
                    case Project.Configuration.OutputType.Exe:
                        break;
                    case Project.Configuration.OutputType.Lib:
                        RemoveIfContains(flags, "/INCREMENTAL");
                        RemoveIfContains(flags, "/DYNAMICBASE");
                        RemoveIfContains(flags, "/DEBUG");
                        RemoveIfContains(flags, "/PDB");
                        RemoveIfContains(flags, "/LARGEADDRESSAWARE");
                        RemoveIfContains(flags, "/OPT:REF");
                        RemoveIfContains(flags, "/OPT:ICF");
                        RemoveIfContains(flags, "/OPT:NOREF");
                        RemoveIfContains(flags, "/OPT:NOICF");
                        RemoveIfContains(flags, "/FUNCTIONPADMIN");
                        break;
                    case Project.Configuration.OutputType.Dll:
                    default:
                        break;
                }

                return flags;
            }
            private Strings FilterClangLinkerFlags(Strings flags, GenerationContext context)
            {
                switch (context.Configuration.Output)
                {
                    case Project.Configuration.OutputType.Exe:
                        break;
                    case Project.Configuration.OutputType.Lib:
                        break;
                    case Project.Configuration.OutputType.Dll:
                        break;
                    default:
                        break;
                }

                return flags;
            }
            private Strings FilterGccLinkerFlags(Strings flags, GenerationContext context)
            {
                switch (context.Configuration.Output)
                {
                    case Project.Configuration.OutputType.Exe:
                        break;
                    case Project.Configuration.OutputType.Lib:
                        break;
                    case Project.Configuration.OutputType.Dll:
                        break;
                    default:
                        break;
                }

                return flags;
            }
            private Strings ConvertLibraryDependencyFiles(GenerationContext context)
            {
                Strings result = new Strings();
                foreach (var libFile in context.Configuration.DependenciesLibraryFiles)
                {
                    if (context.Configuration.DependenciesOtherLibraryFiles.Contains(libFile))
                    {
                        result.Add(libFile);
                        continue;
                    }

                    string stem = Path.GetFileNameWithoutExtension(libFile);
                    string extension = Path.GetExtension(libFile);

                    string fullFileName = $"{stem}_{context.Configuration.Target.ProjectConfigurationName}_{context.Compiler}{extension}";
                    result.Add(fullFileName);
                }
                return result;
            }

            private string GetPreBuildCommands(GenerationContext context)
            {
                string preBuildCommand = "";
                string suffix = " && ";

                foreach (var command in context.Configuration.EventPreBuild)
                {
                    preBuildCommand += command;
                    preBuildCommand += suffix;
                }

                // remove trailing && if possible
                if (preBuildCommand.EndsWith(suffix))
                {
                    preBuildCommand = preBuildCommand.Substring(0, preBuildCommand.Length - suffix.Length);
                }

                return preBuildCommand;
            }

            private string GetPostBuildCommands(GenerationContext context)
            {
                string postBuildCommand = "";
                string suffix = " && ";

                foreach (var command in context.Configuration.EventPostBuild)
                {
                    postBuildCommand += command;
                    postBuildCommand += suffix;
                }

                // remove trailing && if possible
                if (postBuildCommand.EndsWith(suffix))
                {
                    postBuildCommand = postBuildCommand.Substring(0, postBuildCommand.Length - suffix.Length);
                }

                return postBuildCommand;
            }

            private void RemoveIfContains(Strings flags, string value)
            {
                flags.RemoveAll(x => x.StartsWith(value));
            }
        }

        public class ProjectFile
        {
            // Custom converter that we use to write the configurations
            public class ConfigConverter : JsonConverter<Dictionary<Compiler, CompilerConfiguration>>
            {
                public override void Write(Utf8JsonWriter writer, Dictionary<Compiler, CompilerConfiguration> value, JsonSerializerOptions options)
                {
                    // Configs are structured per compiler, per config
                    writer.WriteStartObject(); // Write the opening brace

                    foreach (var kvp in value)
                    {
                        Compiler compiler = kvp.Key;
                        WriteCompilerConfigs(writer, options, compiler, kvp.Value);
                    }
                    
                    writer.WriteEndObject(); // Write the closing brace
                }

                public override Dictionary<Compiler, CompilerConfiguration> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                {
                    // Implement the Read method if needed
                    throw new NotImplementedException();
                }

                private void WriteCompilerConfigs(Utf8JsonWriter writer, JsonSerializerOptions options, Compiler compiler, CompilerConfiguration config)
                {
                    writer.WritePropertyName(compiler.ToString().ToLower()); // write compiler name

                    writer.WriteStartObject();  // Write the opening brace
                    
                    foreach (var kvp2 in config.configs)
                    {
                        writer.WritePropertyName(kvp2.Key); // write config name
                        JsonSerializer.Serialize(writer, kvp2.Value, options); // write config settings
                    }
                    
                    writer.WriteEndObject(); // Write the closing brace
                }
            }

            public class ProjectFileConfig
            {
                public string ninja_file { get; set; }
                public List<string> dependencies { get; set; }

                public ProjectFileConfig(Compiler compiler, Project.Configuration config)
                {
                    ninja_file = GetPerConfigFilePath(config, compiler);
                    dependencies = GetBuildDependencies(config);
                }
            }

            public class CompilerConfiguration
            {
                public Dictionary<string, ProjectFileConfig> configs { get; set; }

                private Compiler CompilerName;

                public CompilerConfiguration(Compiler compilerName)
                {
                    CompilerName = compilerName;
                    configs = new Dictionary<string, ProjectFileConfig>();
                }

                public void Add(Project.Configuration config)
                {
                    configs.Add(config.Target.ProjectConfigurationName.ToLower(), new ProjectFileConfig(CompilerName, config));
                }
            }

            public string name { get; set; }

            [JsonConverter(typeof(ConfigConverter))]
            public Dictionary<Compiler, CompilerConfiguration> configs { get; set; }
            
            public ProjectFile(string projectName, List<Project.Configuration> configurations)
            {
                name = projectName;
                configs = new Dictionary<Compiler, CompilerConfiguration>();

                // Loop over all the configs of this project and link compiler with configs
                foreach (var config in configurations)
                {
                    Compiler Compiler = config.Target.GetFragment<Compiler>();
                    if (configs.ContainsKey(Compiler) == false)
                    {
                        configs.Add(Compiler, new CompilerConfiguration(Compiler));
                    }

                    configs[Compiler].Add(config);
                }
            }
        }

        private static readonly string NinjaExtension = ".ninja";
        private static readonly string ProjectExtension = ".nproj";
        private static Repository Repo = new Repository(Directory.GetCurrentDirectory());

        // Take in a list of flags and merge them into 1 string
        private static string MergeMultipleFlagsToString(Strings options, bool addQuotes = false, string perOptionPrefix = "")
        {
            string result = "";
            foreach (var option in options)
            {
                if (option == "REMOVE_LINE_TAG")
                {
                    continue;
                }

                result += " ";
                result += perOptionPrefix;
                if (addQuotes)
                {
                    result += "\"";
                }
                result += option;
                if (addQuotes)
                {
                    result += "\"";
                }
            }
            return result;
        }
        // Take in a list of flags and merge them into 1 string
        private static string MergeMultipleFlagsToString(OrderableStrings options, bool addQuotes = false, string perOptionPrefix = "")
        {
            string result = "";
            foreach (var option in options)
            {
                if (option == "REMOVE_LINE_TAG")
                {
                    continue;
                }

                result += " ";
                result += perOptionPrefix;
                if (addQuotes)
                {
                    result += "\"";
                }
                result += option;
                if (addQuotes)
                {
                    result += "\"";
                }
            }
            return result;
        }

        private static bool IsFileModifiedFromGit(string filepath)
        {
            // Set the ignore case config value to true or we get errors here
            Repo.Config.Set("core.ignorecase", true);
            FileStatus status = Repo.RetrieveStatus(filepath);
            return (status & FileStatus.ModifiedInIndex) != 0 || (status & FileStatus.ModifiedInWorkdir) != 0;
        }

        // This is the main generation function for ninja project files
        // First we generate ninja files per configuartion
        // Later we create the project files which will link to these per config ninja files
        public void Generate(
        Builder builder,
        Project project,
        List<Project.Configuration> configurations,
        string projectFilePath,
        List<string> generatedFiles,
        List<string> skipFiles)
        {
            // Loop over each configuration and generate a ninja file for each one of them
            foreach (var config in configurations)
            {
                GenerationContext context = new GenerationContext(builder, projectFilePath, project, config);

                if (config.Output == Project.Configuration.OutputType.Dll && context.Compiler == Compiler.GCC)
                {
                    throw new Error("Shared library for GCC is currently not supported");
                }

                Strings filesToCompile = GetFilesToCompile(project, config);

                // If we support unity files, we need to update the files we use for compilation
                // to use the unity files instead and not the actual source files of the project
                if (config.Options.Contains(Options.Vc.Compiler.JumboBuild.Enable))
                {
                    filesToCompile = GenerateUnityFiles(context, filesToCompile);
                }

                // Generate the per config ninja file
                WritePerConfigFile(context, filesToCompile, generatedFiles, skipFiles);
            }

            // Generate the ninja project file which links all the ninja files together
            WriteProjectFile(builder, project, configurations, generatedFiles, skipFiles);
        }

        // This is the main generation function for ninja solution files
        public void Generate(
            Builder builder,
            Solution solution,
            List<Solution.Configuration> configurations,
            string solutionFile,
            List<string> generatedFiles,
            List<string> skipFiles)
        {
            List<Project> projects = new List<Project>();
            foreach (var config in configurations)
            {
                foreach (var projectInfo in config.IncludedProjectInfos)
                {
                    if (projects.Contains(projectInfo.Project) == false)
                    {
                        projects.Add(projectInfo.Project);
                    }
                }
            }

            StringBuilder sb = new StringBuilder();
            string quote = "\"";

            string trailingCharacters = ",\n";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                trailingCharacters = ",\r\n";
            }

            sb.AppendLine($"{{");

            foreach (Project project in projects)
            {
                sb.AppendLine($"\t{quote}{project.Name}{quote} : {quote}{FullProjectPath(project)}{quote},");
            }
            sb.Remove(sb.Length - trailingCharacters.Length, 1); // remove trailing comma

            sb.AppendLine($"}}");

            string content = sb.ToString();
            content = content.Replace("\\", "\\\\");

            FileGenerator fileGenerator = new FileGenerator();
            fileGenerator.WriteLine(content);
            MemoryStream memoryStream = fileGenerator.ToMemoryStream();
            FileInfo solutionFileInfo = new FileInfo($"{solutionFile}{Util.GetSolutionExtension(DevEnv.ninja)}");

            if (builder.Context.WriteGeneratedFile(solution.GetType(), solutionFileInfo, memoryStream))
            {
                generatedFiles.Add(solutionFileInfo.FullName);
            }
            else
            {
                skipFiles.Add(solutionFileInfo.FullName);
            }
        }

        private Strings GenerateUnityFiles(GenerationContext context, Strings filesToCompile)
        {
            Strings result = new Strings();

            UnityFilesGenerator unityFilesGenerator = new UnityFilesGenerator(context.Configuration.MaxFilesPerUnityFile, context.Configuration.IntermediatePath);

            foreach (string fileToCompile in filesToCompile)
            {
                // Modified files are not added in unity builds as it's faster to exclude them and compile them seperately
                bool isModified = IsFileModifiedFromGit(fileToCompile);
                if (isModified)
                {
                    context.Builder.LogWriteLine($"Excluding {fileToCompile} from unity build as its modified");
                }

                // of course we don't want to include files the user has specified that we shouldn't
                bool isExcludeFromJumboBuild = context.Configuration.ResolvedSourceFilesExcludeFromJumboBuild.Contains(fileToCompile);

                // when not adding to unity file, we add the source file to the result already
                if (isModified || isExcludeFromJumboBuild)
                {
                    result.Add(fileToCompile);
                }
                else
                {
                    unityFilesGenerator.AddFile(fileToCompile);
                }
            }

            result.AddRange(unityFilesGenerator.Generate());
            return result;
        }

        // Write the ninja project file which links
        // all the ninja files generated for this project together
        private void WriteProjectFile(Builder builder, Project project, List<Project.Configuration> configurations, List<string> generatedFiles, List<string> skipFiles)
        {
            ProjectFile projectFile = new ProjectFile(project.Name, configurations);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true, // Makes it a bit more human friendly
            };

            string jsonBlob = JsonSerializer.Serialize(projectFile, options);
            jsonBlob = jsonBlob.ToLower();

            var fileGenerator = new FileGenerator();
            fileGenerator.WriteVerbatim(jsonBlob);
            string fullProjectPath = FullProjectPath(project);

            if (SaveFileGeneratorToDisk(fileGenerator, builder, project, $"{fullProjectPath}"))
            {
                generatedFiles.Add(fullProjectPath);
            }
            else
            {
                skipFiles.Add(fullProjectPath);
            }
        }

        // Get the build dependencies (dependencies that need to be build before this config of this project gets build)
        // The build dependencies are listed in json format
        private static List<string> GetBuildDependencies(Project.Configuration configuration)
        {
            List<string> buildDependencies = new List<string>();

            // A static lib doesn't have any build dependencies
            if (configuration.Output == Project.Configuration.OutputType.Lib)
            {
                return buildDependencies;
            }

            foreach (var config in configuration.ResolvedDependencies)
            {
                string projectPath = FullProjectPath(config.Project);
                buildDependencies.Add(projectPath);
            }

            return buildDependencies;
        }

        // Given a project, it'll find the project path where it should be generated
        private static string FullProjectPath(Project project)
        {
            foreach (var config in project.Configurations)
            {
                if (config.Target.GetFragment<DevEnv>() == DevEnv.ninja)
                {
                    string projectPath = Path.Combine(config.ProjectPath, project.Name);
                    return $"{projectPath}{ProjectExtension}";
                }
            }

            throw new Error("Failed to find project path");
        }

        // Write the ninja file that's unique for this configuration
        private void WritePerConfigFile(GenerationContext context, Strings filesToCompile, List<string> generatedFiles, List<string> skipFiles)
        {
            // the obj file paths act as output for the compiler statements
            // but they're input for the linker statements
            Strings objFilePaths = GetObjPaths(context, filesToCompile);

            ResolvePdbPaths(context);
            GenerateConfOptions(context);

            List<CompileStatement> compileStatements = GenerateCompileStatements(context, filesToCompile, objFilePaths);
            List<LinkStatement> linkStatements = GenerateLinkingStatements(context, objFilePaths);

            var fileGenerator = new FileGenerator();

            GenerateHeader(fileGenerator);

            fileGenerator.WriteLine("");
            fileGenerator.WriteLine($"builddir = {Path.Combine(context.Configuration.IntermediatePath, ".ninja")}");

            GenerateRules(fileGenerator, context);

            fileGenerator.RemoveTaggedLines();

            fileGenerator.WriteLine("# Compile statements");
            foreach (var compileStatement in compileStatements)
            {
                fileGenerator.WriteLine(compileStatement.ToString());
            }

            fileGenerator.WriteLine("");

            fileGenerator.WriteLine("# Link statements");
            foreach (var linkStatement in linkStatements)
            {
                fileGenerator.WriteLine(linkStatement.ToString());
            }

            GenerateProjectBuilds(fileGenerator, context);

            string filePath = GetPerConfigFilePath(context.Configuration, context.Compiler);

            if (SaveFileGeneratorToDisk(fileGenerator, context.Builder, context.Project, filePath))
            {
                generatedFiles.Add(filePath);
            }
            else
            {
                skipFiles.Add(filePath);
            }
        }

        // Get the filename for a ninja file that's unique for its configuration
        private static string GetPerConfigFileName(Project.Configuration config, Compiler compiler)
        {
            return $"{config.Project.Name}.{config.Target.ProjectConfigurationName}.{compiler}{NinjaExtension}";
        }

        // Get the full filepath of a ninja file that's unique for its configuration
        private static string GetPerConfigFilePath(Project.Configuration config, Compiler compiler)
        {
            return Path.Combine(config.ProjectPath, "ninja", GetPerConfigFileName(config, compiler));
        }

        // Write the value out if its not empty
        // Don't write anything if it is empty
        private static void WriteIfNotEmpty(FileGenerator fileGenerator, string key, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                fileGenerator.WriteLine($"{key} = {value}");
            }
        }

        // Write the value out if its not empty
        // Write the "or" value if it is empty
        private static void WriteIfNotEmptyOr(FileGenerator fileGenerator, string key, string value, string orValue)
        {
            if (!string.IsNullOrEmpty(value))
            {
                fileGenerator.WriteLine($"{key} = {value}");
            }
            else
            {
                fileGenerator.WriteLine($"{key} = {orValue}");
            }
        }

        // The filename of the  
        private static string UniqueOutputFilename(string targetFileFullName, string configName, string compiler, string targetFileFullExtension)
        {
            return $"{targetFileFullName}_{configName}_{compiler}{targetFileFullExtension}";

        }

        // The full target filepath for the context
        private static string FullTargetPath(GenerationContext context)
        {
            string fullFileName = UniqueOutputFilename(context.Configuration.TargetFileFullName, context.Configuration.Target.ProjectConfigurationName, context.Compiler.ToString(), context.Configuration.TargetFileFullExtension);
            return Path.Combine(context.Configuration.TargetPath, fullFileName);
        }

        // The full target filepath in ninja format for the context
        private static string FullNinjaTargetPath(GenerationContext context)
        {
            return ConvertToNinjaFilePath(FullTargetPath(context));
        }

        // Change relative pdb path setting to disable
        // Set the compiler and linker pdb suffixes
        // Create the PDB folders if necessary
        private void ResolvePdbPaths(GenerationContext context)
        {
            // Relative pdb filepaths is not supported for ninja generation
            if (context.Configuration.UseRelativePdbPath == true)
            {
                Util.LogWrite("Warning: Configuration.UseRelativePdbPath is not supported for ninja generation");
                context.Configuration.UseRelativePdbPath = false;
            }

            // Resolve pdb filepath so it's sorted per compiler
            context.Configuration.CompilerPdbSuffix = $"{context.Compiler}{context.Configuration.CompilerPdbSuffix}";
            context.Configuration.LinkerPdbSuffix = $"{context.Compiler}{context.Configuration.LinkerPdbSuffix}";

            // Not all compilers generate the directories to pdb files
            CreatePdbPath(context);
        }

        // Write content of fileGenerator to disk
        private bool SaveFileGeneratorToDisk(FileGenerator fileGenerator, Builder builder, Project project, string filePath)
        {
            MemoryStream memoryStream = fileGenerator.ToMemoryStream();
            FileInfo projectFileInfo = new FileInfo(filePath);
            return builder.Context.WriteGeneratedFile(project.GetType(), projectFileInfo, memoryStream);
        }

        // Get the source files that are required to compile the project in the specified configuration
        private Strings GetFilesToCompile(Project project, Project.Configuration configuration)
        {
            Strings filesToCompile = new Strings();

            // Loop over all the source files and exclude those we don't want to build
            foreach (var sourceFile in project.ResolvedSourceFiles)
            {
                string extension = Path.GetExtension(sourceFile);
                if (project.SourceFilesCompileExtensions.Contains(extension) && !configuration.ResolvedSourceFilesBuildExclude.Contains(sourceFile))
                {
                    filesToCompile.Add(sourceFile);
                }
            }

            return filesToCompile;
        }

        // Get all the obj paths for the files to compile
        Strings GetObjPaths(GenerationContext context, Strings filesToCompile)
        {
            Strings objFilePaths = new Strings();

            foreach (var sourceFile in filesToCompile)
            {
                string pathRelativeToSourceRoot = Util.PathGetRelative(context.Project.SourceRootPath, sourceFile);
                string fileStem = Path.GetFileNameWithoutExtension(pathRelativeToSourceRoot);
                string fileDir = Path.GetDirectoryName(pathRelativeToSourceRoot);
                string outputExtension = context.Configuration.Target.GetFragment<Compiler>() == Compiler.MSVC ? ".obj" : ".o";
                string objPath = $"{Path.Combine(context.Configuration.IntermediatePath, fileDir, fileStem)}{outputExtension}";
                objFilePaths.Add(objPath);
            }

            return objFilePaths;
        }

        // Create the pdb path for the context if needed
        private void CreatePdbPath(GenerationContext context)
        {
            if (!Directory.Exists(Path.GetDirectoryName(context.Configuration.LinkerPdbFilePath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(context.Configuration.LinkerPdbFilePath));
            }
        }

        // Get the path of the C++ Compiler for the context
        private string GetCppCompilerPath(GenerationContext context)
        {
            return KitsRootPaths.GetCompilerSettings(context.Compiler).BinPathForCppCompiler;
        }

        // Get the path of the C Compiler for the context
        private string GetCCompilerPath(GenerationContext context)
        {
            return KitsRootPaths.GetCompilerSettings(context.Compiler).BinPathForCCompiler;
        }

        // Static libs are best totally removed before they get regenerated
        // This avoids symbol clashes
        // As static libs are just compressed obj files put togehter (and no linking gets done)
        // To make it easy for the archiver, we just generate them from scratch every time
        private string DeleteOutputIfExists(GenerationContext context)
        {
            string targetPath = FullTargetPath(context);
            return $"cmd.exe /C if exist \"{targetPath}\" del \"{targetPath}\"";
        }

        // Get the of the Linker for the intermediate files generated for the context
        private string GetLinkerPath(GenerationContext context)
        {
            return context.Configuration.Output == Project.Configuration.OutputType.Lib
                ? KitsRootPaths.GetCompilerSettings(context.Compiler).ArchiverPath
                : KitsRootPaths.GetCompilerSettings(context.Compiler).LinkerPath;
        }

        // Write out the header to the file generator.
        // This header is shared by all ninja files generated through Sharpmake
        private void GenerateHeader(FileGenerator fileGenerator)
        {
            fileGenerator.WriteLine($"# !! Sharpmake generated file !!");
            fileGenerator.WriteLine($"# All edits will be overwritten on the next sharpmake run");
            fileGenerator.WriteLine($"#");
            fileGenerator.WriteLine($"# Make sure we have the right version of Ninja");
            fileGenerator.WriteLine($"ninja_required_version = 1.1");
            fileGenerator.WriteLine($"");
        }

        // Generate the rules that specify what we support from a ninja file
        // This is often just the compile, link, clean and compdb generation rule
        private void GenerateRules(FileGenerator fileGenerator, GenerationContext context)
        {
            // Compilation
            string depsValue = "gcc";
            if (context.Compiler == Compiler.MSVC)
            {
                fileGenerator.WriteLine($"msvc_deps_prefix = Note: including file:");
                fileGenerator.WriteLine($"");
                depsValue = "msvc";
            }

            fileGenerator.WriteLine($"# Rules to specify how to do things");
            fileGenerator.WriteLine($"");
            fileGenerator.WriteLine($"# Rule for compiling C++ files using {context.Compiler}");
            fileGenerator.WriteLine($"{Template.RuleBegin} {Template.RuleStatement.CompileCppFile(context)}");
            fileGenerator.WriteLine($"  depfile = $out.d");
            fileGenerator.WriteLine($"  deps = {depsValue}");
            fileGenerator.WriteLine($"{Template.CommandBegin}\"{GetCppCompilerPath(context)}\" ${Template.BuildStatement.Defines(context)} ${Template.BuildStatement.SystemIncludes(context)} ${Template.BuildStatement.Includes(context)} ${Template.BuildStatement.CompilerFlags(context)} ${Template.BuildStatement.CompilerImplicitFlags(context)} {Template.Input}");
            fileGenerator.WriteLine($"{Template.DescriptionBegin} Building C++ object $out");

            fileGenerator.WriteLine($"");
            
            fileGenerator.WriteLine($"# Rule for compiling C files using {context.Compiler}");
            fileGenerator.WriteLine($"{Template.RuleBegin} {Template.RuleStatement.CompileCFile(context)}");
            fileGenerator.WriteLine($"  depfile = $out.d");
            fileGenerator.WriteLine($"  deps = {depsValue}");
            fileGenerator.WriteLine($"{Template.CommandBegin}\"{GetCCompilerPath(context)}\" ${Template.BuildStatement.Defines(context)} ${Template.BuildStatement.SystemIncludes(context)} ${Template.BuildStatement.Includes(context)} ${Template.BuildStatement.CompilerFlags(context)} ${Template.BuildStatement.CompilerImplicitFlags(context)} {Template.Input}");
            fileGenerator.WriteLine($"{Template.DescriptionBegin} Building C object $out");

            fileGenerator.WriteLine($"");

            // Linking
            string description = context.Configuration.Output == Project.Configuration.OutputType.Exe
                ? "Linking C++ executable"
                : "Creating C++ archive";

            // for a static lib, which just a collection of obj files
            // we remove the previously generated one if it exists
            // as that's supposed to be faster than letting the toolchain
            // update the symbols of an existing one
            string impliedPrebuild = context.Configuration.Output == Project.Configuration.OutputType.Lib
                ? $" && {DeleteOutputIfExists(context)}"
                : "";

            fileGenerator.WriteLine($"# Rule for linking C++ objects");
            fileGenerator.WriteLine($"{Template.RuleBegin}{Template.RuleStatement.LinkToUse(context)}");
            fileGenerator.WriteLine($"{Template.CommandBegin}cmd.exe /C ${Template.BuildStatement.PreBuild(context)} {impliedPrebuild} && \"{GetLinkerPath(context)}\" ${Template.BuildStatement.ImplicitLinkerFlags(context)} ${Template.BuildStatement.LinkerFlags(context)} ${Template.BuildStatement.ImplicitLinkerPaths(context)} ${Template.BuildStatement.ImplicitLinkerLibraries(context)} ${Template.BuildStatement.LinkerPaths(context)} ${Template.BuildStatement.LinkerLibraries(context)} ${Template.BuildStatement.LinkerResponseFile(context)} && ${Template.BuildStatement.PostBuild(context)}\"");
            fileGenerator.WriteLine($"{Template.DescriptionBegin}{description} ${Template.BuildStatement.TargetFile(context)}");
            fileGenerator.WriteLine($"  restat = $RESTAT");
            fileGenerator.WriteLine($"");

            // Cleaning
            fileGenerator.WriteLine($"# Rule to clean all built files");
            fileGenerator.WriteLine($"{Template.RuleBegin}{Template.RuleStatement.Clean(context)}");
            fileGenerator.WriteLine($"{Template.CommandBegin}{KitsRootPaths.GetNinjaPath()} -f {GetPerConfigFilePath(context.Configuration, context.Compiler)} -t clean");
            fileGenerator.WriteLine($"{Template.DescriptionBegin}Cleaning all build files");
            fileGenerator.WriteLine($"");

            // Compiler DB
            fileGenerator.WriteLine($"# Rule to generate compiler db");
            fileGenerator.WriteLine($"{Template.RuleBegin}{Template.RuleStatement.CompilerDB(context)}");
            fileGenerator.WriteLine($"{Template.CommandBegin}{KitsRootPaths.GetNinjaPath()} -f {GetPerConfigFilePath(context.Configuration, context.Compiler)} -t compdb {Template.RuleStatement.CompileCppFile(context)} {Template.RuleStatement.CompileCFile(context)}");
            fileGenerator.WriteLine($"");
        }

        // Generate a compile statement for each source file we have to compile
        private List<CompileStatement> GenerateCompileStatements(GenerationContext context, Strings filesToCompile, Strings objPaths)
        {
            List<CompileStatement> statements = new List<CompileStatement>();

            for (int i = 0; i < filesToCompile.Count; ++i)
            {
                string fileToCompile = filesToCompile.ElementAt(i);
                string objPath = objPaths.ElementAt(i);
                statements.Add(new CompileStatement(context, fileToCompile, objPath));
            }

            return statements;
        }

        // Generate a link statement that merges all the obj files into 1 exe, lib or dll
        private List<LinkStatement> GenerateLinkingStatements(GenerationContext context, Strings objFilePaths)
        {
            List<LinkStatement> statements = new List<LinkStatement>();

            string outputPath = FullNinjaTargetPath(context);

            statements.Add(new LinkStatement(context, outputPath, objFilePaths));

            return statements;
        }

        // A phony name is just an alias for another build statement
        private static string GeneratePhonyName(Project.Configuration config, Compiler compiler)
        {
            return $"{ config.Target.ProjectConfigurationName}_{compiler}_{config.TargetFileFullName}".ToLower();
        }

        // Generate the different build statements that act as the main interface
        // These are build, clean and compdb generation
        private void GenerateProjectBuilds(FileGenerator fileGenerator, GenerationContext context)
        {
            //eg. build app.exe: phony d$:\testing\ninjasharpmake\.rex\build\ninja\app\debug\bin\app.exe
            string phony_name = GeneratePhonyName(context.Configuration, context.Compiler);
            fileGenerator.WriteLine($"{Template.BuildBegin}{phony_name}: phony {FullNinjaTargetPath(context)}");
            fileGenerator.WriteLine($"{Template.BuildBegin}{Template.CleanBuildStatement(context)}: {Template.RuleStatement.Clean(context)}");
            fileGenerator.WriteLine($"{Template.BuildBegin}{Template.CompDBBuildStatement(context)}: {Template.RuleStatement.CompilerDB(context)}");
            fileGenerator.WriteLine($"");

            fileGenerator.WriteLine($"default {phony_name}");
        }

        // Convert a filepath to a filepath to be read by ninja
        // This simple adds '$' between a drive letter and colon
        // eg c:\foo.txt -> C$:\foo.txt
        private static string ConvertToNinjaFilePath(string path)
        {
            if (!Path.IsPathRooted(path))
            {
                return path;
            }

            // filepaths are absolute and Ninja doesn't support a ':' in a path
            // We need to prepend '$' to the ':' to make sure Ninja parses it correctly
            string driveLetter = path.Substring(0, 1);
            string filePathWithoutDriveLetter = path.Substring(1);
            return $"{driveLetter}${filePathWithoutDriveLetter}";

        }

        // This converts all the sharpmake settings to compiler and linker specifics settings
        // They're all stored in a map in the generation context
        private static void GenerateConfOptions(GenerationContext context)
        {
            // generate all configuration options once...
            var projectOptionsGen = new GenericProjectOptionsGenerator();
            var projectConfigurationOptions = new Dictionary<Project.Configuration, Options.ExplicitOptions>();
            context.SetProjectConfigurationOptions(projectConfigurationOptions);

            // set generator information
            var configurationTasks = PlatformRegistry.Get<Project.Configuration.IConfigurationTasks>(context.Configuration.Platform);
            context.Configuration.GeneratorSetOutputFullExtensions(
                configurationTasks.GetDefaultOutputFullExtension(Project.Configuration.OutputType.Exe),
                configurationTasks.GetDefaultOutputFullExtension(Project.Configuration.OutputType.Exe),
                configurationTasks.GetDefaultOutputFullExtension(Project.Configuration.OutputType.Dll),
                ".pdb");

            projectConfigurationOptions.Add(context.Configuration, new Options.ExplicitOptions());
            context.CommandLineOptions = new GenericProjectOptionsGenerator.GenericCmdLineOptions();
            context.LinkerCommandLineOptions = new GenericProjectOptionsGenerator.GenericCmdLineOptions();

            projectOptionsGen.GenerateOptions(context);
        }
    }
}

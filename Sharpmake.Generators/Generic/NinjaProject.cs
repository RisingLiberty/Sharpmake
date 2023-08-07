﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Sharpmake.Generators.Generic
{
    public class YesNoEnum
    {
        public enum Value
        {
            No,
            Yes
        }
    }
    public class IsLinkerResponse : YesNoEnum { }

    public partial class NinjaProject : IProjectGenerator
    {
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

        private class CompileStatement
        {
            private string Name;
            private string Input;
            private GenerationContext Context;

            public Strings Defines { get; set; }
            public string DepPath { get; set; }
            public Strings ImplicitCompilerFlags { get; set; }
            public Strings CompilerFlags { get; set; }
            public OrderableStrings Includes { get; set; }
            public OrderableStrings SystemIncludes { get; set; }
            public string TargetFilePath { get; set; }

            public CompileStatement(string name, string input, GenerationContext context)
            {
                Name = name;
                Input = input;
                Context = context;
            }

            public override string ToString()
            {
                var fileGenerator = new FileGenerator();

                string defines = MergeMultipleFlagsToString(Defines, false, CompilerFlagLookupTable.Get(Context.Compiler, CompilerFlag.Define));
                string implicitCompilerFlags = MergeMultipleFlagsToString(ImplicitCompilerFlags);
                string compilerFlags = MergeMultipleFlagsToString(CompilerFlags);
                string includes = MergeMultipleFlagsToString(Includes, true, CompilerFlagLookupTable.Get(Context.Compiler, CompilerFlag.Include));
                string systemIncludes = MergeMultipleFlagsToString(SystemIncludes, true, CompilerFlagLookupTable.Get(Context.Compiler, CompilerFlag.SystemInclude));

                fileGenerator.WriteLine($"{Template.BuildBegin}{Name}: {Template.RuleStatement.CompileCppFile(Context)} {Input}");

                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.Defines(Context)}", defines);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.DepFile(Context)}", $"{DepPath}.d");
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.CompilerImplicitFlags(Context)}", implicitCompilerFlags);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.SystemIncludes(Context)}", systemIncludes);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.CompilerFlags(Context)}", compilerFlags);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.Includes(Context)}", includes);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.TargetPdb(Context)}", TargetFilePath);

                return fileGenerator.ToString();
            }
        }

        private class LinkStatement
        {
            public string ResponseFilePath { get; set; }
            public Strings ObjFilePaths { get; set; }
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
            private string OutputPath;

            public LinkStatement(GenerationContext context, string outputPath)
            {
                Context = context;
                OutputPath = outputPath;

                PreBuild = "cd .";
                PostBuild = "cd .";
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

                // when using the regular linker, we can use additional linker libs, if we're creating a static lib that depends on another however, we can't do this
                // and have to add the archive as an additional input file
                if (Context.Configuration.Output != Project.Configuration.OutputType.Lib)
                {
                    implicitLinkerPaths = MergeMultipleFlagsToString(ImplicitLinkerPaths, true, LinkerFlagLookupTable.Get(Context.Compiler, LinkerFlag.IncludePath));
                    implicitLinkerLibs = MergeMultipleFlagsToString(ImplicitLinkerLibs, false, LinkerFlagLookupTable.Get(Context.Compiler, LinkerFlag.IncludeFile));
                    libraryPaths = MergeMultipleFlagsToString(LinkerPaths, true, LinkerFlagLookupTable.Get(Context.Compiler, LinkerFlag.IncludePath));
                    libraryFiles = MergeMultipleFlagsToString(LinkerLibs, false, LinkerFlagLookupTable.Get(Context.Compiler, LinkerFlag.IncludeFile));
                }

                // generate the link command for this library
                fileGenerator.Write($"{Template.BuildBegin}{CreateNinjaFilePath(FullOutputPath(Context))}: {Template.RuleStatement.LinkToUse(Context)}");
                fileGenerator.Write(" | ");
                
                foreach (string path in ObjFilePaths)
                {
                    fileGenerator.Write($" {path}");
                }

                fileGenerator.WriteLine(GetNinjaDependencyTargets(Context));

                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.LinkerResponseFile(Context)}", $"@{CreateNinjaFilePath(ResponseFilePath)}");
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.ImplicitLinkerFlags(Context)}", implicitLinkerFlags);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.ImplicitLinkerPaths(Context)}", implicitLinkerPaths);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.ImplicitLinkerLibraries(Context)}", implicitLinkerLibs);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.LinkerFlags(Context)}", linkerFlags);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.LinkerPaths(Context)}", libraryPaths);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.LinkerLibraries(Context)}", libraryFiles);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.TargetFile(Context)}", OutputPath);
                WriteIfNotEmpty(fileGenerator, $"  {Template.BuildStatement.TargetPdb(Context)}", TargetPdb);
                WriteIfNotEmptyOr(fileGenerator, $"  {Template.BuildStatement.PreBuild(Context)}", PreBuild, "cd .");
                WriteIfNotEmptyOr(fileGenerator, $"  {Template.BuildStatement.PostBuild(Context)}", PostBuild, "cd .");
                ;

                return fileGenerator.ToString();
            }
        }

        private static readonly string NinjaExtension = ".ninja";
        private static readonly string ProjectExtension = ".nproj";

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
        
        public void Generate(
        Builder builder,
        Project project,
        List<Project.Configuration> configurations,
        string projectFilePath,
        List<string> generatedFiles,
        List<string> skipFiles)
        {
            // The first pass writes ninja files per configuration
            foreach (var config in configurations)
            {
                GenerationContext context = new GenerationContext(builder, projectFilePath, project, config);

                if (config.Output == Project.Configuration.OutputType.Dll && context.Compiler == Compiler.GCC)
                {
                    throw new Error("Shared library for GCC is currently not supported");
                }

                Strings filesToCompile = GetFilesToCompile(project, config);
                WritePerConfigFile(context, filesToCompile, generatedFiles, skipFiles);
            }

            // the second pass uses these files to create project file where the files can be build
            WriteProjectFile(builder, projectFilePath, project, configurations, generatedFiles, skipFiles);
        }

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

        private static string GetNinjaDependencyTargets(GenerationContext context)
        {
            string result = "";
            string prefix = " ";

            if (context.Configuration.ResolvedDependencies.Count() > 0)
            {
                result += prefix;
            }

            foreach (var dependency in context.Configuration.ResolvedDependencies)
            {
                string phony_name = GeneratePhonyName(dependency, dependency.Target.GetFragment<Compiler>());
                result += phony_name;
                result += " ";
            }

            return result;
        }

        private void WriteProjectFile(Builder builder, string projectFilePath, Project project, List<Project.Configuration> configurations, List<string> generatedFiles, List<string> skipFiles)
        {
            string projectName = project.Name;
            Dictionary<Compiler, List<Project.Configuration>> configPerCompiler = new Dictionary<Compiler, List<Project.Configuration>>();

            foreach (var config in configurations)
            {
                GenerationContext context = new GenerationContext(builder, projectFilePath, project, config);
                if (configPerCompiler.ContainsKey(context.Compiler) == false)
                {
                    configPerCompiler.Add(context.Compiler, new List<Project.Configuration>());
                }

                configPerCompiler[context.Compiler].Add(context.Configuration);
            }

            StringBuilder sb = new StringBuilder();
            string quote = "\"";

            string trailingCharacters = ",\n";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                trailingCharacters = ",\r\n";
            }

            sb.AppendLine($"{{");
            sb.AppendLine($"\t{quote}{projectName.ToLower()}{quote}:");
            sb.AppendLine($"\t{{");

            foreach (var compiler in configPerCompiler)
            {
                sb.AppendLine($"\t\t{quote}{compiler.Key.ToString().ToLower()}{quote}:");
                sb.AppendLine($"\t\t{{");
                foreach (var config in compiler.Value)
                {
                    GenerationContext context = new GenerationContext(builder, projectFilePath, project, config);
                    sb.AppendLine($"\t\t\t{quote}{config.Name.ToLower()}{quote}:");
                    sb.AppendLine($"\t\t\t{{");
                    string ninjaFilePath = GetPerConfigFilePath(context.Configuration, context.Compiler);
                    sb.AppendLine($"\t\t\t\t{quote}ninja_file{quote} : {quote}{ninjaFilePath}{quote},");
                    sb.AppendLine($"\t\t\t\t{quote}dependencies{quote} : [");
                    string dependencies = GetDependencies(context, 4);
                    sb.AppendLine($"{dependencies}");
                    sb.AppendLine($"\t\t\t\t]");
                    sb.AppendLine($"\t\t\t}},");
                }
                sb.Remove(sb.Length - trailingCharacters.Length, 1); // remove trailing comma
                sb.AppendLine($"\t\t}},");
            }
            sb.Remove(sb.Length - trailingCharacters.Length, 1); // remove trailing comma

            sb.AppendLine($"\t}}");
            sb.AppendLine($"}}");

            var fileGenerator = new FileGenerator();

            string content = sb.ToString();
            content = content.Replace("\\", "\\\\");
            fileGenerator.WriteLine(content);

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

        private string GetDependencies(GenerationContext context, int indentLevel = 0)
        {
            string result = "";
            string suffix = ",\n";
            string indent = indentLevel > 0 ? Enumerable.Repeat("\t", (int)indentLevel).Aggregate((sum, next) => sum + next) : "";

            foreach (var config in context.Configuration.ResolvedDependencies)
            {
                string projectPath = FullProjectPath(config.Project);

                result += indent;
                result += $"\"{projectPath}\"";
                result += suffix;
            }

            if (result.EndsWith(suffix))
            {
                result = result.Substring(0, result.Length - suffix.Length);
            }

            return result;
        }

        private string FullProjectPath(Project project)
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

        private void GenerateIncludes(FileGenerator fileGenerator, GenerationContext context)
        { 
            foreach (var dependency in context.Configuration.ResolvedDependencies)
            {
                var compiler = dependency.Target.GetFragment<Compiler>();
                var path = GetPerConfigFilePath(dependency, compiler);
                fileGenerator.WriteLine($"include {CreateNinjaFilePath(path)}");
            }
        }

        private void WritePerConfigFile(GenerationContext context, Strings filesToCompile, List<string> generatedFiles, List<string> skipFiles)
        {
            Strings objFilePaths = GetObjPaths(context);

            ResolvePdbPaths(context);
            GenerateConfOptions(context);

            List<CompileStatement> compileStatements = GenerateCompileStatements(context, filesToCompile, objFilePaths);
            List<LinkStatement> linkStatements = GenerateLinking(context, GetNonNinjaObjPaths(context), objFilePaths);

            var fileGenerator = new FileGenerator();

            GenerateHeader(fileGenerator);

            fileGenerator.WriteLine("");

            if (context.Configuration.Output != Project.Configuration.OutputType.Lib)
            {
                GenerateIncludes(fileGenerator, context);
            }

            GenerateRules(fileGenerator, context);

            fileGenerator.RemoveTaggedLines();

            foreach (var compileStatement in compileStatements)
            {
                fileGenerator.WriteLine(compileStatement.ToString());
            }

            foreach (var linkStatement in linkStatements)
            {
                fileGenerator.WriteLine(linkStatement.ToString());
            }

            GenerateProjectBuilds(fileGenerator, context);

            string filePath = GetPerConfigFilePath(context.Configuration, context.Compiler);

            if (SaveFileGeneratorToDisk(fileGenerator, context, filePath))
            {
                generatedFiles.Add(filePath);
            }
            else
            {
                skipFiles.Add(filePath);
            }
        }

        private void WriteCompilerDatabaseFile(GenerationContext context)
        {
            string outputFolder = Directory.GetParent(GetCompilerDBPath(context)).FullName;
            string outputPath = GetCompilerDBPath(context);
            if (!Directory.Exists(outputFolder))
            {
                Directory.CreateDirectory(outputFolder);
            }
            else if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            
            string ninjaFilePath = KitsRootPaths.GetNinjaPath();
            string command = $"-f {GetPerConfigFilePath(context.Configuration, context.Compiler)} {Template.CompDBBuildStatement(context)} --quiet >> {outputPath}";

            Process process = new Process();
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/C {ninjaFilePath} {command}";
            process.StartInfo = startInfo;
            process.Start();
        }

        private string GetPerConfigFileName(Project.Configuration config, Compiler compiler)
        {
            return $"{config.Project.Name}.{config.Name}.{compiler}{NinjaExtension}";
        }

        private string GetCompilerDBPath(GenerationContext context)
        {
            return $"{Path.Combine(context.Configuration.ProjectPath, "clang_tools", $"{Template.PerConfigFolderFormat(context)}", "compile_commands.json")}";
        }

        private string GetPerConfigFilePath(Project.Configuration config, Compiler compiler)
        {
            return Path.Combine(config.ProjectPath, "ninja", GetPerConfigFileName(config, compiler));
        }

        private static void WriteIfNotEmpty(FileGenerator fileGenerator, string key, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                fileGenerator.WriteLine($"{key} = {value}");
            }
        }
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

        private static string FullOutputPath(GenerationContext context)
        {
            string fullFileName = $"{context.Configuration.TargetFileFullName}_{context.Configuration.Name}_{context.Compiler}{context.Configuration.TargetFileFullExtension}";
            return CreateNinjaFilePath($"{Path.Combine(context.Configuration.TargetPath, fullFileName)}");
        }

        private static string CreateResponseFile(GenerationContext context, IsLinkerResponse.Value isLinkerRespponse, Strings files)
        {
            string fullFileName = isLinkerRespponse == IsLinkerResponse.Value.Yes
                ? $"{context.Configuration.TargetFileFullName}_{context.Configuration.Name}_{context.Compiler}_linker_response.txt"
                : $"{context.Configuration.TargetFileFullName}_{context.Configuration.Name}_{context.Compiler}_compiler_response.txt";
            string responseFilePath = Path.Combine(context.Configuration.TargetPath, fullFileName);

            StringBuilder sb = new StringBuilder();
            foreach (string file in files)
            {
                sb.Append($"{file.Replace('\\', '/')} ");
            }

            File.WriteAllText(responseFilePath, sb.ToString());
            return responseFilePath;
        }

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

        private bool SaveFileGeneratorToDisk(FileGenerator fileGenerator, GenerationContext context, string filePath)
        {
            return SaveFileGeneratorToDisk(fileGenerator, context.Builder, context.Project, filePath);
        }

        private bool SaveFileGeneratorToDisk(FileGenerator fileGenerator, Builder builder, Project project, string filePath)
        {
            MemoryStream memoryStream = fileGenerator.ToMemoryStream();
            FileInfo projectFileInfo = new FileInfo(filePath);
            return builder.Context.WriteGeneratedFile(project.GetType(), projectFileInfo, memoryStream);
        }

        private Strings GetFilesToCompile(Project project, Project.Configuration configuration)
        {
            Strings filesToCompile = new Strings();

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
        Strings GetObjPaths(GenerationContext context)
        {
            Strings objFilePaths = new Strings();

            foreach (var sourceFile in context.Project.ResolvedSourceFiles)
            {
                string extension = Path.GetExtension(sourceFile);
                if (context.Project.SourceFilesCompileExtensions.Contains(extension) && !context.Configuration.ResolvedSourceFilesBuildExclude.Contains(sourceFile))
                {
                    string pathRelativeToSourceRoot = Util.PathGetRelative(context.Project.SourceRootPath, sourceFile);
                    string fileStem = Path.GetFileNameWithoutExtension(pathRelativeToSourceRoot);
                    string fileDir = Path.GetDirectoryName(pathRelativeToSourceRoot);

                    string outputExtension = context.Configuration.Target.GetFragment<Compiler>() == Compiler.MSVC ? ".obj" : ".o";

                    string objPath = $"{Path.Combine(context.Configuration.IntermediatePath, fileDir, fileStem)}{outputExtension}";
                    objFilePaths.Add(CreateNinjaFilePath(objPath));
                }
            }

            return objFilePaths;
        }

        Strings GetNonNinjaObjPaths(GenerationContext context)
        {
            Strings objFilePaths = new Strings();

            foreach (var sourceFile in context.Project.ResolvedSourceFiles)
            {
                string extension = Path.GetExtension(sourceFile);
                if (context.Project.SourceFilesCompileExtensions.Contains(extension) && !context.Configuration.ResolvedSourceFilesBuildExclude.Contains(sourceFile))
                {
                    string pathRelativeToSourceRoot = Util.PathGetRelative(context.Project.SourceRootPath, sourceFile);
                    string fileStem = Path.GetFileNameWithoutExtension(pathRelativeToSourceRoot);
                    string fileDir = Path.GetDirectoryName(pathRelativeToSourceRoot);

                    string outputExtension = context.Configuration.Target.GetFragment<Compiler>() == Compiler.MSVC ? ".obj" : ".o";

                    string objPath = $"{Path.Combine(context.Configuration.IntermediatePath, fileDir, fileStem)}{outputExtension}";
                    objFilePaths.Add(objPath);
                }
            }

            return objFilePaths;
        }

        private void CreatePdbPath(GenerationContext context)
        {
            if (!Directory.Exists(Path.GetDirectoryName(context.Configuration.LinkerPdbFilePath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(context.Configuration.LinkerPdbFilePath));
            }
        }

        private string GetCompilerPath(GenerationContext context)
        {
            return KitsRootPaths.GetCompilerSettings(context.Compiler).BinPath;
        }

        private string GetLinkerPath(GenerationContext context)
        {
            return context.Configuration.Output == Project.Configuration.OutputType.Lib
                ? KitsRootPaths.GetCompilerSettings(context.Compiler).ArchiverPath
                : KitsRootPaths.GetCompilerSettings(context.Compiler).LinkerPath;
        }

        private void GenerateHeader(FileGenerator fileGenerator)
        {
            fileGenerator.WriteLine($"# !! Sharpmake generated file !!");
            fileGenerator.WriteLine($"# All edits will be overwritten on the next sharpmake run");
            fileGenerator.WriteLine($"#");
            fileGenerator.WriteLine($"# Make sure we have the right version of Ninja");
            fileGenerator.WriteLine($"ninja_required_version = 1.1");
            fileGenerator.WriteLine($"builddir = .ninja");
            fileGenerator.WriteLine($"");
        }

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
            fileGenerator.WriteLine($"{Template.CommandBegin}\"{GetCompilerPath(context)}\" ${Template.BuildStatement.Defines(context)} ${Template.BuildStatement.SystemIncludes(context)} ${Template.BuildStatement.Includes(context)} ${Template.BuildStatement.CompilerFlags(context)} ${Template.BuildStatement.CompilerImplicitFlags(context)} {Template.Input}");
            fileGenerator.WriteLine($"{Template.DescriptionBegin} Building C++ object $out");
            fileGenerator.WriteLine($"");

            // Linking

            string outputType = context.Configuration.Output == Project.Configuration.OutputType.Exe
                ? "executable"
                : "archive";

            fileGenerator.WriteLine($"# Rule for linking C++ objects");
            fileGenerator.WriteLine($"{Template.RuleBegin}{Template.RuleStatement.LinkToUse(context)}");
            fileGenerator.WriteLine($"{Template.CommandBegin}cmd.exe /C \"${Template.BuildStatement.PreBuild(context)} && \"{GetLinkerPath(context)}\" ${Template.BuildStatement.ImplicitLinkerFlags(context)} ${Template.BuildStatement.LinkerFlags(context)} ${Template.BuildStatement.ImplicitLinkerPaths(context)} ${Template.BuildStatement.ImplicitLinkerLibraries(context)} ${Template.BuildStatement.LinkerPaths(context)} ${Template.BuildStatement.LinkerLibraries(context)} ${Template.BuildStatement.LinkerResponseFile(context)} && ${Template.BuildStatement.PostBuild(context)}\"");
            fileGenerator.WriteLine($"{Template.DescriptionBegin}Linking C++ {outputType} ${Template.BuildStatement.TargetFile(context)}");
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
            fileGenerator.WriteLine($"{Template.CommandBegin}{KitsRootPaths.GetNinjaPath()} -f {GetPerConfigFilePath(context.Configuration, context.Compiler)} -t compdb {Template.RuleStatement.CompileCppFile(context)}");
            fileGenerator.WriteLine($"");
        }

        private List<CompileStatement> GenerateCompileStatements(GenerationContext context, Strings filesToCompile, Strings objPaths)
        {
            List<CompileStatement> statements = new List<CompileStatement>();

            for (int i = 0; i < filesToCompile.Count; ++i)
            {
                string fileToCompile = filesToCompile.ElementAt(i);
                string objPath = objPaths.ElementAt(i);
                string ninjaFilePath = CreateNinjaFilePath(fileToCompile);

                var compileStatement = new CompileStatement(objPath, ninjaFilePath, context);
                compileStatement.Defines = context.Configuration.Defines;
                compileStatement.DepPath = objPath;
                compileStatement.ImplicitCompilerFlags = GetImplicitCompilerFlags(context, objPath);
                compileStatement.CompilerFlags = GetCompilerFlags(context);
                OrderableStrings includePaths = context.Configuration.IncludePaths;
                includePaths.AddRange(context.Configuration.IncludePrivatePaths);
                includePaths.AddRange(context.Configuration.DependenciesIncludePaths);
                compileStatement.Includes = includePaths;
                OrderableStrings systemIncludePaths = context.Configuration.IncludeSystemPaths;
                systemIncludePaths.AddRange(context.Configuration.DependenciesIncludeSystemPaths);
                compileStatement.SystemIncludes = systemIncludePaths;
                compileStatement.TargetFilePath = context.Configuration.LinkerPdbFilePath;

                statements.Add(compileStatement);
            }

            return statements;
        }

        private List<LinkStatement> GenerateLinking(GenerationContext context, Strings nonNinjaobjFilePaths, Strings objFilePaths)
        {
            List<LinkStatement> statements = new List<LinkStatement>();

            string outputPath = FullOutputPath(context);
            string responseFilePath = CreateResponseFile(context, IsLinkerResponse.Value.Yes, nonNinjaobjFilePaths);

            var linkStatement = new LinkStatement(context, outputPath);

            linkStatement.ResponseFilePath = responseFilePath;
            linkStatement.ObjFilePaths = objFilePaths;
            linkStatement.ImplicitLinkerFlags = GetImplicitLinkerFlags(context, outputPath);
            linkStatement.Flags = GetLinkerFlags(context);
            linkStatement.ImplicitLinkerPaths = GetImplicitLinkPaths(context);
            Strings linkerPaths = GetLinkerPaths(context);
            if (context.Configuration.Output != Project.Configuration.OutputType.Lib)
            {
                linkerPaths.AddRange(context.Configuration.DependenciesLibraryPaths);
            }
            linkStatement.LinkerPaths = linkerPaths;
            linkStatement.ImplicitLinkerLibs = GetImplicitLinkLibraries(context);
            Strings linkerLibs = GetLinkLibraries(context);
            if (context.Configuration.Output != Project.Configuration.OutputType.Lib)
            {
                linkerLibs.AddRange(ConvertLibraryDependencyFiles(context));
            }
            linkStatement.LinkerLibs = linkerLibs;
            linkStatement.PreBuild = GetPreBuildCommands(context);
            linkStatement.PostBuild = GetPostBuildCommands(context);
            linkStatement.TargetPdb = context.Configuration.LinkerPdbFilePath;

            statements.Add(linkStatement);

            return statements;
        }
        private static string GeneratePhonyName(Project.Configuration config, Compiler compiler)
        {
            return $"{ config.Name }_{ compiler}_{ config.TargetFileFullName}".ToLower();
        }

        private void GenerateProjectBuilds(FileGenerator fileGenerator, GenerationContext context)
        {
            //build app.exe: phony d$:\testing\ninjasharpmake\.rex\build\ninja\app\debug\bin\app.exe
            string phony_name = GeneratePhonyName(context.Configuration, context.Compiler);
            fileGenerator.WriteLine($"{Template.BuildBegin}{phony_name}: phony {FullOutputPath(context)}");
            fileGenerator.WriteLine($"{Template.BuildBegin}{Template.CleanBuildStatement(context)}: {Template.RuleStatement.Clean(context)}");
            fileGenerator.WriteLine($"{Template.BuildBegin}{Template.CompDBBuildStatement(context)}: {Template.RuleStatement.CompilerDB(context)}");
            fileGenerator.WriteLine($"");

            fileGenerator.WriteLine($"default {phony_name}");
        }

        private static string CreateNinjaFilePath(string path)
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

            int index = context.Configuration.Options.FindIndex(x => x.GetType() == typeof(Options.Vc.Compiler.CppLanguageStandard));
            if (index != -1)
            {
                object option = context.Configuration.Options[index];
                Options.Vc.Compiler.CppLanguageStandard cppStandard = (Options.Vc.Compiler.CppLanguageStandard)option;

                //switch (cppStandard)
                //{
                //    case Options.Vc.Compiler.CppLanguageStandard.CPP14:
                //        flags.Add($" {CompilerFlagLookupTable.Get(context.Compiler, CompilerFlag.Define)}_MSVC_LANG=201402L");
                //        break;
                //    case Options.Vc.Compiler.CppLanguageStandard.CPP17:
                //        flags.Add($" {CompilerFlagLookupTable.Get(context.Compiler, CompilerFlag.Define)}_MSVC_LANG=201703L");
                //        break;
                //    case Options.Vc.Compiler.CppLanguageStandard.CPP20:
                //        flags.Add($" {CompilerFlagLookupTable.Get(context.Compiler, CompilerFlag.Define)}_MSVC_LANG=202002L");
                //        break;
                //    case Options.Vc.Compiler.CppLanguageStandard.Latest:
                //        flags.Add($" {CompilerFlagLookupTable.Get(context.Compiler, CompilerFlag.Define)}_MSVC_LANG=202004L");
                //        break;
                //    default:
                //        flags.Add($" {CompilerFlagLookupTable.Get(context.Compiler, CompilerFlag.Define)}_MSVC_LANG=201402L");
                //        break;
                //}
            }

            return flags;
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

        private Strings GetCompilerFlags(GenerationContext context)
        {
            return new Strings(context.CommandLineOptions.Values);
        }

        private Strings GetLinkerPaths(GenerationContext context)
        {
            return new Strings(context.Configuration.LibraryPaths);
        }
        private Strings GetLinkLibraries(GenerationContext context)
        {
            return new Strings(context.Configuration.LibraryFiles);
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

                string fullFileName = $"{stem}_{context.Configuration.Name}_{context.Compiler}{extension}";
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

        private void RemoveIfContains(Strings flags, string value)
        {
            flags.RemoveAll(x => x.StartsWith(value));
        }

        private void GenerateConfOptions(GenerationContext context)
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

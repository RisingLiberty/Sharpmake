using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace Sharpmake
{
    class UnityFilesGenerator
    {
        private int MaxFilesPerUnityFile = 0;
        private string IntermediatePath = "";
        private string UnityFilesDir = "";
        private List<string> Files = new List<string>();


        public UnityFilesGenerator(int maxFilesPerUnityFile, string intermediatePath)
        {
            MaxFilesPerUnityFile = maxFilesPerUnityFile;
            IntermediatePath = intermediatePath;
            UnityFilesDir = Path.Combine(IntermediatePath, "unity");
        }

        public void AddFile(string filePath)
        {
            Files.Add(filePath);
        }

        public List<string> Generate()
        {
            List<string> unityFiles = new List<string>();

            Generators.FileGenerator fileGenerator = new Generators.FileGenerator();
            
            int numUnityFilesAdded = 0;
            foreach (string file in Files)
            {
                AddInclude(fileGenerator, file);
                ++numUnityFilesAdded;

                if (MaxFilesPerUnityFile != 0 && numUnityFilesAdded > MaxFilesPerUnityFile)
                {
                    string unityFileFilename = Path.Combine(UnityFilesDir, $"unity_{unityFiles.Count}.cpp");
                    FileInfo fileInfo = new FileInfo(unityFileFilename);
                    Util.FileWriteIfDifferentInternal(fileInfo, fileGenerator.ToMemoryStream());
                    fileGenerator = new Generators.FileGenerator();
                    unityFiles.Add(unityFileFilename);
                }
            }

            if (fileGenerator.ToString().Length > 0)
            {
                string unityFileFilename = Path.Combine(UnityFilesDir, $"unity_{unityFiles.Count}.cpp");
                FileInfo fileInfo = new FileInfo(unityFileFilename);
                Util.FileWriteIfDifferentInternal(fileInfo, fileGenerator.ToMemoryStream());
                unityFiles.Add(unityFileFilename);
            }

            return unityFiles;
        }

        private void AddInclude(Generators.FileGenerator fileGenerator, string filePath)
        {
            fileGenerator.WriteLine("");
            fileGenerator.WriteLine($"#include \"{filePath.Replace('\\', '/')}\" //NOLINT(bugprone-suspicious-include)");
            fileGenerator.WriteLine("");
        }
    }
}

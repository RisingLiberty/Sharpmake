﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sharpmake
{
    [Fragment, Flags]
    public enum Compiler
    {
        Auto = (1 << 0), // Determine Compiler from DevEnv (eg. Visual Studio -> MSVC)
        MSVC = (1 << 1), // Microsoft and compatible compilers
        Clang = (1 << 2), // Clang and compatible compilers
        GCC = (1 << 3), // GCC and compatible compilers
    }

    public class CompilerInfo
    {
        public CompilerInfo(Compiler compiler, string binPathForCppCompiler, string linkerPath, string archivePath, string ranLibPath, string binPathForCCompiler)
        {
            Compiler = compiler;
            BinPathForCppCompiler = binPathForCppCompiler;
            LinkerPath = linkerPath;
            ArchiverPath = archivePath;
            RanLibPath = ranLibPath;
            BinPathForCCompiler = binPathForCCompiler;
        }

        public Compiler Compiler { get; set; }
        public string BinPathForCppCompiler { get; set; }
        public string LinkerPath { get; set; }
        public string ArchiverPath { get; set; }
        public string RanLibPath { get; set; }
        public string BinPathForCCompiler { get; set; }
    }
}

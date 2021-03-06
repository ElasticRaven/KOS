﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace kOS.Compilation
{
    public class ProgramBuilder
    {
        private Dictionary<Guid, ObjectFile> _objectFiles = new Dictionary<Guid, ObjectFile>();

        /// <summary>
        /// Creates a new ObjectFile with the parts provided
        /// </summary>
        /// <param name="parts">Collection of CodeParts generated by the compiler</param>
        /// <returns>Id of the new ObjectFile</returns>
        public Guid AddObjectFile(IEnumerable<CodePart> parts)
        {
            ObjectFile objectFile = new ObjectFile(parts);
            _objectFiles.Add(objectFile.Id, objectFile);
            return objectFile.Id;
        }

        public void AddRange(IEnumerable<CodePart> parts)
        {
            ObjectFile objectFile;
            
            if (_objectFiles.Count == 0)
            {
                objectFile = new ObjectFile(parts);
                _objectFiles.Add(objectFile.Id, objectFile);
            }
            else
            {
                objectFile = _objectFiles.First().Value;
                objectFile.Parts.AddRange(parts);
            }
        }
        
        public List<Opcode> BuildProgram()
        {
            List<Opcode> program = new List<Opcode>();

            foreach (var objectFile in _objectFiles.Values)
            {
                CodePart linkedObject = new CodePart();

                foreach (var part in objectFile.Parts)
                {
                    AddInitializationCode(linkedObject, part);
                    linkedObject.FunctionsCode.AddRange(part.FunctionsCode);
                    linkedObject.MainCode.AddRange(part.MainCode);
                }

                // we assume that the first object is the main program and the rest are subprograms/libraries
                bool isMainProgram = (objectFile == _objectFiles.Values.First());
                // add a jump to the entry point so the execution skips the functions code
                if (isMainProgram)
                    AddJumpToEntryPoint(linkedObject);
                // add an instruction to indicate the end of the program
                AddEndOfProgram(linkedObject, isMainProgram);
                // save the entry point of the object
                objectFile.EntryPointLabel = GetEntryPointLabel(linkedObject);
                // add the linked object to the final program
                program.AddRange(linkedObject.MergeSections());
            }

            // replace all the labels references with the corresponding address
            ReplaceLabels(program);

            return program;
        }

        protected virtual void AddInitializationCode(CodePart linkedObject, CodePart part)
        {
            linkedObject.InitializationCode.AddRange(part.InitializationCode);
        }

        private void AddJumpToEntryPoint(CodePart linkedObject)
        {
            if (linkedObject.MainCode.Count > 0)
            {
                OpcodeBranchJump jumpOpcode = new OpcodeBranchJump();
                jumpOpcode.DestinationLabel = GetEntryPointLabel(linkedObject);
                linkedObject.FunctionsCode.Insert(0, jumpOpcode);
            }
        }

        private string GetEntryPointLabel(CodePart linkedObject)
        {
            List<Opcode> codeSection = linkedObject.InitializationCode.Count > 0 ? linkedObject.InitializationCode : linkedObject.MainCode;
            return codeSection[0].Label;
        }

        protected virtual void AddEndOfProgram(CodePart linkedObject, bool isMainProgram)
        {
            if (isMainProgram)
            {
                linkedObject.MainCode.Add(new OpcodeEOP());
            }
            else
            {
                linkedObject.MainCode.Add(new OpcodeReturn());
            }
        }

        private void ReplaceLabels(List<Opcode> program)
        {
            Dictionary<string, int> labels = new Dictionary<string, int>();

            // get the index of every label
            for (int index = 0; index < program.Count; index++)
            {
                if (program[index].Label != string.Empty)
                {
                    labels.Add(program[index].Label, index);
                }
            }

            // replace destination labels with the corresponding index
            for (int index = 0; index < program.Count; index++)
            {
                Opcode opcode = program[index];
                if (opcode.DestinationLabel != null && opcode.DestinationLabel != string.Empty)
                {
                    int destinationIndex = labels[opcode.DestinationLabel];
                    if (opcode is BranchOpcode)
                    {
                        ((BranchOpcode)opcode).distance = destinationIndex - index;
                    }
                    else if (opcode is OpcodePush)
                    {
                        ((OpcodePush)opcode).argument = destinationIndex;
                    }
                    else if (opcode is OpcodeCall)
                    {
                        ((OpcodeCall)opcode).destination = destinationIndex;
                    }
                }
            }

            // complete the entry point address of all the objects
            foreach (var objectFile in _objectFiles.Values)
            {
                if (objectFile.EntryPointLabel != string.Empty)
                    objectFile.EntryPointAddress = labels[objectFile.EntryPointLabel];
            }
        }

        public int GetObjectFileEntryPointAddress(Guid objectFileId)
        {
            if (_objectFiles.ContainsKey(objectFileId))
            {
                return _objectFiles[objectFileId].EntryPointAddress;
            }
            else
            {
                return 0;
            }
        }


        private class ObjectFile
        {
            public Guid Id { get; private set; }
            public List<CodePart> Parts { get; private set; }
            public string EntryPointLabel { get; set; }
            public int EntryPointAddress { get; set; }

            public ObjectFile(IEnumerable<CodePart> parts)
            {
                Id = Guid.NewGuid();
                Parts = parts.ToList();
            }
        }

    }
}

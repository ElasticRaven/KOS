﻿using System;
using System.Collections.Generic;
using System.Linq;
using kOS.Factories;
using UnityEngine;
using KSP.IO;
using kOS.InterProcessor;
using kOS.Binding;
using kOS.Persistence;
using kOS.Suffixed;
using kOS.AddOns.RemoteTech2;

namespace kOS.Module
{
    public class kOSProcessor : PartModule
    {
        public enum Modes { READY, STARVED, OFF };
        public Modes Mode = Modes.READY;

        public Harddisk HardDisk { get; private set; }
        private int vesselPartCount;
        private SharedObjects shared;

        //640K ought to be enough for anybody -sic
        private const int PROCESSOR_HARD_CAP = 655360;

        [KSPField(isPersistant = true, guiName = "kOS Disk Space", guiActive = true)]
        public int diskSpace = 500;

        [KSPField(isPersistant = true, guiActive = false)] public int MaxPartId = 100;

        [KSPEvent(guiActive = true, guiName = "Open Terminal", category = "skip_delay;")]
        public void Activate()
        {
            Debug.Log("kOS: Activate");
            Core.OpenWindow(shared);
        }

        [KSPField(isPersistant = true, guiName = "Required Power", guiActive = true)] 
        public float RequiredPower;

        [KSPEvent(guiActive = true, guiName = "Toggle Power")]
        public void TogglePower()
        {
            Debug.Log("kOS: Toggle Power");
            Modes newMode = (Mode != Modes.OFF) ? Modes.OFF : Modes.STARVED;
            SetMode(newMode);
        }
        
        [KSPAction("Open Terminal", actionGroup = KSPActionGroup.None)]
        public void Activate(KSPActionParam param)
        {
            Debug.Log("kOS: Open Terminal from Dialog");
            Activate();
        }

        [KSPAction("Close Terminal", actionGroup = KSPActionGroup.None)]
        public void Deactivate(KSPActionParam param)
        {
            Debug.Log("kOS: Close Terminal from ActionGroup");
            Core.CloseWindow(shared);
        }

        [KSPAction("Toggle Power", actionGroup = KSPActionGroup.None)]
        public void TogglePower(KSPActionParam param)
        {
            Debug.Log("kOS: Toggle Power from ActionGroup");
            TogglePower();
        }

        public override void OnStart(StartState state)
        {
            //Do not start from editor and at KSP first loading
            if (state == StartState.Editor || state == StartState.None)
            {
                return;
            }

            Debug.Log(string.Format("kOS: OnStart: {0} {1}", state, Mode));
            InitObjects();
        }

        public void InitObjects()
        {
            Debug.LogWarning("kOS: InitObjects: " + (shared == null));

            shared = new SharedObjects();
            CreateFactory();
                    
            shared.Vessel = vessel;
            shared.Processor = this;
            shared.UpdateHandler = new UpdateHandler();
            shared.BindingMgr = new BindingManager(shared);
            shared.Interpreter = shared.Factory.CreateInterpreter(shared);
            shared.Screen = shared.Interpreter;
            shared.ScriptHandler = new Compilation.KS.KSScript();
            shared.Logger = new KSPLogger(shared);
            shared.VolumeMgr = new VolumeManager(shared);
            shared.ProcessorMgr = new ProcessorManager(shared);
            shared.Cpu = new Execution.CPU(shared);

            // initialize the file system
            shared.VolumeMgr.Add(shared.Factory.CreateArchive());
            if (HardDisk == null) HardDisk = new Harddisk(Mathf.Min(diskSpace, PROCESSOR_HARD_CAP));
            shared.VolumeMgr.Add(HardDisk);
            if (!Config.GetInstance().StartOnArchive) shared.VolumeMgr.SwitchTo(HardDisk);
        }

        private void CreateFactory()
        {
            Debug.LogWarning("kOS: Starting Factory Building");
            bool isAvailable;
            try
            {
                isAvailable = RemoteTechHook.IsAvailable(vessel.id);
            }
            catch
            {
                isAvailable = false;
            }

            if (isAvailable)
            {
                Debug.LogWarning("kOS: RemoteTech Factory Building");
                shared.Factory = new RemoteTechFactory();
            }
            else
            {
                Debug.LogWarning("kOS: Standard Factory Building");
                shared.Factory = new StandardFactory();
            }
        }

        public void RegisterkOSExternalFunction(object[] parameters)
        {
            //Debug.Log("*** External Function Registration Succeeded");
            //cpu.RegisterkOSExternalFunction(parameters);
        }
        
        public static int AssignNewId()
        {
            var config = PluginConfiguration.CreateForType<kOSProcessor>();
            config.load();
            var id = config.GetValue<int>("CpuIDMax") + 1;
            config.SetValue("CpuIDMax", id);
            config.save();

            return id;
        }
        
        public void Update()
        {
            if (shared == null) return;

            if (part.State == PartStates.DEAD)
            {
                Mode = Modes.OFF;
                return;
            }

            if (shared != null && shared.Vessel != vessel)
            {
                shared.Vessel = vessel;
            }

            if (Mode == Modes.READY)
            {
                if (shared.UpdateHandler != null) shared.UpdateHandler.UpdateObservers(Time.deltaTime);
                UpdateParts();
            }

            ProcessElectricity(part, TimeWarp.fixedDeltaTime);
        }
        
        public void UpdateParts()
        {
            // Trigger whenever the number of parts in the vessel changes (like when staging, docking or undocking)
            if (vessel.parts.Count == vesselPartCount) return;

            var missingHardDisks = false;
            var attachedVolumes = new List<Volume>();
            var processors = new List<kOSProcessor>();

            // Look for all the processors that exists in the vessel
            foreach (var partObj in vessel.parts)
            {
                kOSProcessor processorPart;
                if (!PartIsKosProc(partObj, out processorPart)) continue;

                processors.Add(processorPart);

                // A harddisk may be null because the kOS part haven't been initialized yet
                // Wait until the next update and everything should be fine
                if (processorPart.HardDisk != null)
                {
                    attachedVolumes.Add(processorPart.HardDisk);
                }
                else
                {
                    missingHardDisks = true;
                    break;
                }
            }

            if (missingHardDisks) return;

            shared.VolumeMgr.UpdateVolumes(attachedVolumes);
            shared.ProcessorMgr.UpdateProcessors(processors);
            vesselPartCount = vessel.parts.Count;
        }

        public bool PartIsKosProc(Part input, out kOSProcessor proc)
        {
            foreach (var processor in input.Modules.OfType<kOSProcessor>())
            {
                proc = processor;
                return true;
            }

            proc = null;
            return false;
        }

        public override void OnFixedUpdate()
        {
        }

        public override void OnInactive()
        {
            Debug.Log("kOS: Processor Stop");
        }

        public override void OnLoad(ConfigNode node)
        {
            // KSP Seems to want to make an instance of my partModule during initial load
            if (vessel == null) return;

            if (node.HasNode("harddisk"))
            {
                var newDisk = new Harddisk(node.GetNode("harddisk"));
                HardDisk = newDisk;
            }

            InitObjects();

            if (shared != null && shared.Cpu != null)
            {
                shared.Cpu.OnLoad(node);
            }
            base.OnLoad(node);
        }

        public override void OnSave(ConfigNode node)
        {
            if (HardDisk != null)
            {
                ConfigNode hdNode = HardDisk.Save("harddisk");
                node.AddNode(hdNode);
            }

            if (shared != null && shared.Cpu != null)
            {
                shared.Cpu.OnSave(node);
                Config.GetInstance().SaveConfig();
            }

            base.OnSave(node);
        }

        private void ProcessElectricity(Part partObj, float time)
        {
            if (Mode == Modes.OFF) return;

            RequiredPower = shared.VolumeMgr.CurrentRequiredPower;
            var electricReq = time * RequiredPower;
            var result = partObj.RequestResource("ElectricCharge", electricReq) / electricReq;

            var newMode = (result < 0.5f) ? Modes.STARVED : Modes.READY;
            SetMode(newMode);
        }

        public void SetMode(Modes newMode)
        {
            if (newMode != Mode)
            {
                switch (newMode)
                {
                    case Modes.READY:
                        if (Mode == Modes.STARVED && shared.Cpu != null) shared.Cpu.Boot();
                        if (shared.Interpreter != null) shared.Interpreter.SetInputLock(false);
                        if (shared.Window != null) shared.Window.SetPowered(true);
                        break;

                    case Modes.OFF:
                    case Modes.STARVED:
                        if (shared.Interpreter != null) shared.Interpreter.SetInputLock(true);
                        if (shared.Window != null) shared.Window.SetPowered(false);
                        break;
                }

                Mode = newMode;
            }
        }

        public void ExecuteInterProcCommand(InterProcCommand command)
        {
            if (command != null)
            {
                command.Execute(shared);
            }
        }
    }
}

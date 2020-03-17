using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Text;
using System;
using Sandbox.Game.Gui;
using Sandbox.Game.Replication;
using VRage.Collections;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Game;
using VRage;
using VRageMath;



namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        // settings
        private const float VMain = 1f; // sets the motor RPM, I used 1 for testing and .5 in survival ?
        private const bool IsLargeGrid = false; // sets values for maxExtension and
        private const bool HasAntiRotor = false; // check if the build has a secondary rotor that guarantees a stable docking port

        // derived variables
        private readonly int _pistonCountZ = 0;
        private readonly int _pistonCountR = 0;
        private readonly float _VPistonZ = 0;
        private readonly float _VPistonR = 0;
        private readonly float _maxExtension = 2f;
        private readonly float _extensionSteps = 0.2f;

        // blocks
        private readonly IMyMotorStator _rotor;
        
        private readonly IMyMotorStator _antiRotor;
        private readonly IMyShipDrill _drill;
        private readonly IMyBeacon _beacon;
        private readonly List<IMyPistonBase> _pistonsR = new List<IMyPistonBase>();
        private readonly List<IMyPistonBase> _pistonsZ = new List<IMyPistonBase>();
        private readonly List<IMyCargoContainer> _containers = new List<IMyCargoContainer>();
        private readonly IMyTextSurface _statusTextPanel;

        // state
        private bool _isExtending = false;
        private bool _isInitializing = false;
        private bool _isResetting = false;
        private bool _backwards = false;
        private bool _isPaused = false;
        private bool _cargoFull = false;
        private int _progress = 0;
        private float _totalMaxVolume = 0;
        private float _totalCurrentVolume = 0;
 


        public Program()
        {
            // TODO: load miner state if exists

            //Set Update frequency to every 100th game tick
            Runtime.UpdateFrequency = UpdateFrequency.Update100;

            // Init Components:
        
            _drill = GridTerminalSystem.GetBlockWithName("Drill") as IMyShipDrill;
            _rotor = GridTerminalSystem.GetBlockWithName("Rotor") as IMyMotorStator;
            if (HasAntiRotor)
            {
                _antiRotor = GridTerminalSystem.GetBlockWithName("AntiRotor") as IMyMotorStator;
            }
       
            _beacon = GridTerminalSystem.GetBlockWithName("Beacon") as IMyBeacon;

            // select all pistons of the piston R group
            GridTerminalSystem.GetBlockGroupWithName("PistonsR").GetBlocksOfType(_pistonsR);
            // select all pistons of the piston Z group
            GridTerminalSystem.GetBlockGroupWithName("PistonsZ").GetBlocksOfType(_pistonsZ);

            // select all containers  
            GridTerminalSystem.GetBlocksOfType(_containers, block => block.IsSameConstructAs(Me));

            // select status LCD
            _statusTextPanel = GridTerminalSystem.GetBlockWithName("Status LCD") as IMyTextPanel;
            _statusTextPanel.ContentType = ContentType.TEXT_AND_IMAGE;

            // set Piston Count and velocity
            _pistonCountZ = _pistonsZ.Count;
            _pistonCountR = _pistonsR.Count;
            _VPistonZ = VMain / (_pistonCountZ * 60);
            _VPistonR = VMain / (_pistonCountR * 60);

            if (IsLargeGrid)
            {
                _maxExtension = 10f;
                _extensionSteps = 1f;
            }

        }

        public void Save()
        {
            // save state
        }

        public void Main(string argument, UpdateType updateSource)
        {
            
            switch (argument)
            {
                case "init":
                    Runtime.UpdateFrequency = UpdateFrequency.Update100;
                    InitMiner();
                    HandleMinerState();
                    break;
                case "stop":
                    PauseMining("STOPPED by user");
                    Runtime.UpdateFrequency = UpdateFrequency.None;
                    break;
                case "reset":
                    Runtime.UpdateFrequency = UpdateFrequency.Update100;
                    ResetMiner();
                    HandleMinerState();
                    break;
                case "continue":
                    Runtime.UpdateFrequency = UpdateFrequency.Update100;
                    ContinueMining();
                    break;
                default:
                    HandleMinerState();
                    break;
            }
        }


        public void HandleMinerState()
        {
            CalcProgress();
            UpdateCargoCapacity();
            UpdateStatusLCD();
        
            //Check if isInitializing
            if (_isInitializing && _pistonsR[0].CurrentPosition == 0f && _pistonsZ[0].CurrentPosition == _extensionSteps)         
            {
                    _rotor.SetValue("Velocity", VMain);
                    _pistonsR.ForEach(piston => piston.SetValue("Velocity", _VPistonR));
                    _pistonsZ.ForEach(piston => piston.SetValue("Velocity", _VPistonZ));
                    _isInitializing = false;
                    SetBeaconMsg(_progress + "%");
               
            }
            else if (_isResetting && _pistonsR[0].CurrentPosition == 0)
            {
               
                    TerminalBlockExtentions.ApplyAction(_rotor, "OnOff_Off");
                    TerminalBlockExtentions.ApplyAction(_drill, "OnOff_Off");
                    _pistonsR.ForEach(piston => TerminalBlockExtentions.ApplyAction(piston, "OnOff_Off"));
                    _pistonsZ.ForEach(piston =>
                    {
                        piston.SetValue("Velocity", -1f);
                        piston.SetValue("LowerLimit", 0f);
                    });
                    _isResetting = false;

                    //stop script
                    Runtime.UpdateFrequency = UpdateFrequency.None;
                    //change beacon signal to finished
                    SetBeaconMsg("Finished");

            }
            else if (!_isPaused && _totalCurrentVolume >= _totalMaxVolume*0.98)
            {
                _cargoFull = true;
                PauseMining("CARGO CONTAINER FULL");
            }

            else if (_isPaused && (_totalCurrentVolume / _totalMaxVolume) < 0.9)
            {
                _cargoFull = false;
                ContinueMining();
            }

            RunMining();
        }

        public void RunMining()
        {
            if (_isExtending && _pistonsZ[0].CurrentPosition == _pistonsZ[0].MaxLimit)
            {
                
                    _isExtending = false;
                    _pistonsR.ForEach(piston => piston.Reverse());
                    _backwards = !_backwards;
                  
            }
            else 
            {
                if (_pistonsR[0].CurrentPosition == (_backwards ? 0f : _maxExtension))
                {
                    var currentZLimit = _pistonsZ[0].MaxLimit;
                    if (currentZLimit == 10f)
                    {
                        ResetMiner();
                    }
                    _isExtending = true;
                    _pistonsZ.ForEach(piston => piston.SetValue("UpperLimit", currentZLimit + 1));

                }
            }
            SetBeaconMsg(_progress + "%");
        }

        public void SetBeaconMsg(string msg)
        {  
             var hudText = new StringBuilder(Me.CubeGrid.CustomName + ": " + msg);
            _beacon.SetValue("HudText", hudText);
        }

        public void CalcProgress()
        {
            var depth = _pistonsZ[0].CurrentPosition;
            var rad = _pistonsR[0].CurrentPosition;
            var res = ((depth - _extensionSteps) * _maxExtension + _extensionSteps * (_backwards ? _maxExtension - rad : rad )) / (_maxExtension*_maxExtension)*100;
            _progress = (int)Math.Floor(res);
        }

        public void InitMiner()
        {
            _isInitializing = true;
            // turn drill on
            TerminalBlockExtentions.ApplyAction(_drill, "OnOff_On");

            // set Values for every Piston
            _pistonsR.ForEach(
                piston =>
                {
                    piston.SetValue("UpperLimit", _maxExtension);
                    piston.SetValue("LowerLimit", 0f);
                    piston.SetValue("Velocity", -0.1f);
                    TerminalBlockExtentions.ApplyAction(piston, "OnOff_On");
                });

            _pistonsZ.ForEach(
                piston =>
                {
                    piston.SetValue("UpperLimit", _extensionSteps);
                    piston.SetValue("LowerLimit", _extensionSteps);
                    piston.SetValue("Velocity", piston.CurrentPosition < _extensionSteps ? 0.1f : -0.1f);
                    TerminalBlockExtentions.ApplyAction(piston, "OnOff_On");
                });

            // set Values for the main rotor and the stabilizing rotor
            _rotor.SetValue("Torque", 300000f);
            _rotor.SetValue("BrakingTorque", 300000f);
            TerminalBlockExtentions.ApplyAction(_rotor, "OnOff_On");

            if (HasAntiRotor)
            {
                _antiRotor.SetValue("Torque", 0f);
                _antiRotor.SetValue("BrakingTorque", 0f);
            }



            SetBeaconMsg("initializing...");
        }

        public void PauseMining(string msg)
        {
            TerminalBlockExtentions.ApplyAction(_rotor, "OnOff_Off");
            _pistonsR.ForEach(piston => TerminalBlockExtentions.ApplyAction(piston, "OnOff_Off"));
            _pistonsZ.ForEach(piston => TerminalBlockExtentions.ApplyAction(piston, "OnOff_Off"));
            TerminalBlockExtentions.ApplyAction(_drill, "OnOff_Off");
            SetBeaconMsg(msg);
            _isPaused = true;
        }

        public void ContinueMining()
        {
            TerminalBlockExtentions.ApplyAction(_rotor, "OnOff_On");
            _pistonsR.ForEach(piston => TerminalBlockExtentions.ApplyAction(piston, "OnOff_On"));
            _pistonsZ.ForEach(piston => TerminalBlockExtentions.ApplyAction(piston, "OnOff_On"));
            TerminalBlockExtentions.ApplyAction(_drill, "OnOff_On");
            SetBeaconMsg(_progress + "%");
            _isPaused = false;
        }
        public void ResetMiner()
        {
            _isResetting = true;

            // invert radial pistons to slowly grind the way back
            _pistonsR.ForEach(
                piston =>
                {
                    piston.SetValue("UpperLimit", 0f); 
                    piston.SetValue("Velocity", -0.1f);
                    TerminalBlockExtentions.ApplyAction(piston, "OnOff_On");
                });

        }

        public void UpdateCargoCapacity()
        {
            var totalMaxVolume = 0f;
            var totalCurrentVolume = 0f;
            foreach (var container in _containers)
            {
                var containerInventory = container.GetInventory();
                var maxVolume = (float)containerInventory.MaxVolume * 1000;
                var currentVolume = (float)containerInventory.CurrentVolume * 1000;
                totalMaxVolume += maxVolume;
                totalCurrentVolume += currentVolume;
            }

            _totalMaxVolume = totalMaxVolume;
            _totalCurrentVolume = totalCurrentVolume;
        }

        public void UpdateStatusLCD()
        {
            _statusTextPanel.WriteText("Miner Status Information");

           
                //_statusTextPanel.WriteText("\n\n...Script Execution Argument: " + arg, true);
     
            
            if (_isInitializing)
            {
                _statusTextPanel.WriteText("\n\n...initializing", true);
            }

            if (_isResetting)
            {
                _statusTextPanel.WriteText("\n\n ...resetting", true);
            }

            if (_isPaused)
            {
                _statusTextPanel.WriteText("\n\n Paused!", true);
            }

            if (_cargoFull)
            {
                _statusTextPanel.WriteText("\n\n CARGO FULL", true);
            }


            if (!_isInitializing && !_isResetting && !_isPaused)
            {
                _statusTextPanel.WriteText("\n\n ....running", true);
            }

            _statusTextPanel.WriteText("\n\n Drill Status: " + (_drill.Enabled ? "running" : "stopped"), true);
            _statusTextPanel.WriteText("\n\n Progress: " + _progress + "%", true);
            _statusTextPanel.WriteText("\n\n Cargo: " + _totalCurrentVolume + " / " + _totalMaxVolume, true);

            //debug info
            //_statusTextPanel.WriteText("\n\n RPos: " + _pistonsR[0].CurrentPosition, true);
            //_statusTextPanel.WriteText("\n\n ZPos: " + _pistonsZ[0].CurrentPosition, true);
            //_statusTextPanel.WriteText("\n\n Piston Z Count: " + _pistonCountZ, true);
            //_statusTextPanel.WriteText("\n Piston Z target Velocity: " + _VPistonZ, true);
            //_statusTextPanel.WriteText("\n Piston Z current Velocity: " + _pistonsZ[0].Velocity, true);

            //_statusTextPanel.WriteText("\n Piston R Count: " + _pistonCountR, true);
            //_statusTextPanel.WriteText("\n Piston R target Velocity: " + _VPistonR, true);
            //_statusTextPanel.WriteText("\n Piston R current Velocity: " + _pistonsR[0].Velocity, true);

        }

    }
}

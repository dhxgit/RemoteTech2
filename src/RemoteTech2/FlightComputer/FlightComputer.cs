﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace RemoteTech
{
    public class FlightComputer : IEnumerable<DelayedCommand>, IDisposable
    {
        public enum State
        {
            Normal = 0,
            Packed = 2,
            OutOfPower = 4,
            NoConnection = 8,
            NotMaster = 16,
        }

        public bool InputAllowed
        {
            get
            {
                var satellite = RTCore.Instance.Network[mParent.Guid];
                var connection = RTCore.Instance.Network[satellite];
                return (satellite != null && satellite.HasLocalControl) || (mParent.Powered && connection.Any());
            }
        }

        public double Delay
        {
            get
            {
                var satellite = RTCore.Instance.Network[mParent.Guid];
                if (satellite != null && satellite.HasLocalControl) return 0.0;
                var connection = RTCore.Instance.Network[satellite];
                if (!connection.Any()) return Double.PositiveInfinity;
                return connection.Min().Delay;
            }
        }

        public State Status
        {
            get
            {
                var satellite = RTCore.Instance.Network[mParent.Guid];
                var connection = RTCore.Instance.Network[satellite];
                var status = State.Normal;
                if (!mParent.Powered) status |= State.OutOfPower;
                if (!mParent.IsMaster) status |= State.NotMaster;
                if (!connection.Any()) status |= State.NoConnection;
                if (mVessel.packed) status |= State.Packed;
                return status;
            }
        }

        public Vector3d Maneuver { get { return mCurrentCommand.ManeuverCommand != null ? mCurrentCommand.ManeuverCommand.Node.GetBurnVector(mVessel.orbit) : mVessel.obt_velocity.normalized; } }
        public ITargetable Target { get { return mCurrentCommand.TargetCommand != null ? mCurrentCommand.TargetCommand.Target : mVessel.mainBody; } }

        public double TotalDelay { get; set; }

        public List<Action<FlightCtrlState>> SanctionedPilots { get; private set; }

        private ISignalProcessor mParent;
        private Vessel mVessel;

        private DelayedCommand mCurrentCommand = AttitudeCommand.Off();
        private FlightCtrlState mPreviousFcs = new FlightCtrlState();
        private readonly List<DelayedCommand> mCommandBuffer = new List<DelayedCommand>();
        private readonly PriorityQueue<DelayedFlightCtrlState> mFlightCtrlBuffer = new PriorityQueue<DelayedFlightCtrlState>();
        private readonly PriorityQueue<DelayedCommand> mManeuverBuffer = new PriorityQueue<DelayedCommand>();

        private Vector3 mLastVelocity;
        private Quaternion mKillRot;
        private FlightComputerWindow mWindow;
        public FlightComputerWindow Window { get { if (mWindow != null) mWindow.Hide(); return mWindow = new FlightComputerWindow(this); } }

        public FlightComputer(ISignalProcessor s)
        {
            mParent = s;
            mVessel = s.Vessel;
            mPreviousFcs.CopyFrom(mVessel.ctrlState);
            SanctionedPilots = new List<Action<FlightCtrlState>>();
        }

        public void Dispose()
        {
            RTLog.Notify("FlightComputer: Dispose");
            if (mVessel != null)
            {
                mVessel.OnFlyByWire -= OnFlyByWirePre;
                mVessel.OnFlyByWire -= OnFlyByWirePost;
            }
            if (mWindow != null)
            {
                mWindow.Hide();
            }
        }

        public void Enqueue(DelayedCommand fc)
        {
            if (!InputAllowed) return;
            if (mVessel.packed) return;

            fc.TimeStamp += Delay;
            if (fc.CancelCommand == null && fc.TargetCommand == null && fc.ManeuverCommand == null)
            {
                fc.ExtraDelay += Math.Max(0, TotalDelay - Delay);
            }

            int pos = mCommandBuffer.BinarySearch(fc);
            if (pos < 0)
            {
                mCommandBuffer.Insert(~pos, fc);
            }
        }

        public void OnUpdate()
        {
            if (!mParent.IsMaster) return;

            // Re-attach periodically
            mVessel.OnFlyByWire -= OnFlyByWirePre;
            mVessel.OnFlyByWire -= OnFlyByWirePost;
            mVessel = mParent.Vessel;
            mVessel.OnFlyByWire = OnFlyByWirePre + mVessel.OnFlyByWire + OnFlyByWirePost;

            PopCommand();
        }

        public void OnFixedUpdate()
        {
            // Send updates for Target / Maneuver
            if ((mCurrentCommand.TargetCommand == null && FlightGlobals.fetch.VesselTarget != null) ||
                (mCurrentCommand.TargetCommand != null && mCurrentCommand.TargetCommand.Target != FlightGlobals.fetch.VesselTarget))
            {
                if (!mCommandBuffer.Any(dc => dc.TargetCommand.Target == FlightGlobals.fetch.VesselTarget))
                {
                    Enqueue(TargetCommand.WithTarget(FlightGlobals.fetch.VesselTarget));
                }
            }
            if (mVessel.patchedConicSolver != null && mVessel.patchedConicSolver.maneuverNodes != null)
            {
                if (mVessel.patchedConicSolver.maneuverNodes.Count > 0 && (mCurrentCommand.ManeuverCommand == null || mCurrentCommand.ManeuverCommand.Node.DeltaV != mVessel.patchedConicSolver.maneuverNodes[0].DeltaV))
                {
                    if (!mManeuverBuffer.Any(dc => dc.ManeuverCommand.Node.DeltaV == mVessel.patchedConicSolver.maneuverNodes[0].DeltaV))
                    {
                        var command = ManeuverCommand.WithNode(mVessel.patchedConicSolver.maneuverNodes[0]);
                        command.TimeStamp += Delay;
                        mManeuverBuffer.Enqueue(command);
                    }
                }
            }

            if (mVessel != mParent.Vessel)
            {
                mVessel.VesselSAS.LockHeading(mVessel.transform.rotation, false);
                mCurrentCommand.ManeuverCommand = null;
                mCommandBuffer.RemoveAll(dc => dc.ManeuverCommand != null);
                SanctionedPilots.Clear();
            }
        }

        private void Enqueue(FlightCtrlState fs)
        {
            DelayedFlightCtrlState dfs = new DelayedFlightCtrlState(fs);
            dfs.TimeStamp += Delay;
            mFlightCtrlBuffer.Enqueue(dfs);
        }

        private void PopFlightCtrlState(FlightCtrlState fcs, ISatellite sat)
        {
            FlightCtrlState delayed = new FlightCtrlState();
            while (mFlightCtrlBuffer.Count > 0 && mFlightCtrlBuffer.Peek().TimeStamp <= RTUtil.GameTime)
            {
                delayed = mFlightCtrlBuffer.Dequeue().State;
            }

            fcs.CopyFrom(delayed);
        }

        private void PopCommand()
        {
            // Maneuvers
            while (mManeuverBuffer.Count > 0 && mManeuverBuffer.Peek().TimeStamp <= RTUtil.GameTime)
            {
                mCurrentCommand.ManeuverCommand = mManeuverBuffer.Dequeue().ManeuverCommand;
            }

            // Commands
            if (mCommandBuffer.Count > 0)
            {
                var delete = new List<DelayedCommand>();
                var time = TimeWarp.deltaTime;
                if (RTSettings.Instance.ThrottleTimeWarp && TimeWarp.CurrentRate > 1.0f)
                {
                    for (int i = 0; i < mCommandBuffer.Count && mCommandBuffer[i].TimeStamp <= RTUtil.GameTime + time * 2; i++)
                    {
                        var dc = mCommandBuffer[i];
                        var message = new ScreenMessage("[Flight Computer]: Throttling back time warp...", 4.0f, ScreenMessageStyle.UPPER_LEFT);
                        while (TimeWarp.deltaTime > (Math.Max(dc.TimeStamp - RTUtil.GameTime, 0) + dc.ExtraDelay) / 2 && TimeWarp.CurrentRate > 1.0f)
                        {
                            TimeWarp.SetRate(TimeWarp.CurrentRateIndex - 1, true);
                            ScreenMessages.PostScreenMessage(message, true);
                        }
                    }
                }

                for (int i = 0; i < mCommandBuffer.Count && mCommandBuffer[i].TimeStamp <= RTUtil.GameTime; i++)
                {
                    var dc = mCommandBuffer[i];
                    if (dc.ExtraDelay > 0)
                    {
                        if (mParent.Powered)
                        {
                            dc.ExtraDelay -= TimeWarp.deltaTime;
                        }
                    }
                    else
                    {
                        if (mVessel.packed)
                        {
                            continue;
                        }
                        if (dc.ActionGroupCommand != null)
                        {
                            KSPActionGroup ag = dc.ActionGroupCommand.ActionGroup;
                            mVessel.ActionGroups.ToggleGroup(ag);
                            if (ag == KSPActionGroup.Stage && !FlightInputHandler.fetch.stageLock)
                            {
                                Staging.ActivateNextStage();
                                ResourceDisplay.Instance.Refresh();
                            }
                            if (ag == KSPActionGroup.RCS)
                            {
                                FlightInputHandler.fetch.rcslock = !FlightInputHandler.RCSLock;
                            }
                        }

                        if (dc.AttitudeCommand != null)
                        {
                            mKillRot = mVessel.transform.rotation;
                            mCurrentCommand.AttitudeCommand = dc.AttitudeCommand;
                            if (dc.AttitudeCommand.Mode == FlightMode.Off)
                            {
                                FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
                            }
                        }

                        if (dc.BurnCommand != null)
                        {
                            mLastVelocity = mVessel.obt_velocity;
                            mCurrentCommand.BurnCommand = dc.BurnCommand;
                        }

                        if (dc.EventCommand != null)
                        {
                            dc.EventCommand.BaseEvent.Invoke();
                        }

                        if (dc.TargetCommand != null)
                        {
                            mCurrentCommand.TargetCommand = dc.TargetCommand.Target != null ? dc.TargetCommand : null;
                        }

                        if (dc.CancelCommand != null)
                        {
                            mCommandBuffer.Remove(dc.CancelCommand);
                            if (mCurrentCommand == dc.CancelCommand)
                            {
                                mCurrentCommand = AttitudeCommand.Off();
                                FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
                            }
                            mCommandBuffer.Remove(dc);
                        }
                        else
                        {
                            mCommandBuffer.RemoveAt(i--);
                        }

                    }
                }
            }
        }

        private void Autopilot(FlightCtrlState fs)
        {
            if (mCurrentCommand.AttitudeCommand != null)
                switch (mCurrentCommand.AttitudeCommand.Mode)
                {
                    case FlightMode.Off:
                        break;
                    case FlightMode.KillRot:
                        HoldOrientation(fs, mKillRot * Quaternion.AngleAxis(90, Vector3.left));
                        break;
                    case FlightMode.AttitudeHold:
                        HoldAttitude(fs);
                        break;
                    case FlightMode.AltitudeHold:
                        break;
                }

            Burn(fs);
        }

        private void Burn(FlightCtrlState fs)
        {
            if (mCurrentCommand.BurnCommand != null)
            {
                if (mCurrentCommand.BurnCommand.Duration > 0)
                {
                    fs.mainThrottle = mCurrentCommand.BurnCommand.Throttle;
                    mCurrentCommand.BurnCommand.Duration -= TimeWarp.deltaTime;
                }
                else if (mCurrentCommand.BurnCommand.DeltaV > 0)
                {
                    fs.mainThrottle = mCurrentCommand.BurnCommand.Throttle;
                    mCurrentCommand.BurnCommand.DeltaV -= (mLastVelocity - mVessel.obt_velocity).magnitude;
                    mLastVelocity = mVessel.obt_velocity;
                }
                else
                {
                    fs.mainThrottle = 0.0f;
                    mCurrentCommand.BurnCommand = null;
                }
            }
        }

        private void HoldOrientation(FlightCtrlState fs, Quaternion target)
        {
            //mVessel.VesselSAS.LockHeading(target * Quaternion.AngleAxis(90, Vector3.right), true);
            mVessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            kOS.SteeringHelper.SteerShipToward(target, fs, mVessel);
            //FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
        }

        private void HoldAttitude(FlightCtrlState fs)
        {
            Vessel v = mVessel;
            Vector3 forward = Vector3.zero;
            Vector3 up = Vector3.zero;
            Quaternion rotationReference;
            switch (mCurrentCommand.AttitudeCommand.Frame)
            {
                case ReferenceFrame.Orbit:
                    forward = v.GetObtVelocity();
                    up = (v.mainBody.position - v.CoM);
                    break;
                case ReferenceFrame.Surface:
                    forward = v.GetSrfVelocity();
                    up = (v.mainBody.position - v.CoM);
                    break;
                case ReferenceFrame.North:
                    up = (v.mainBody.position - v.CoM);
                    forward = Vector3.Exclude(up,
                        v.mainBody.position + v.mainBody.transform.up * (float)v.mainBody.Radius - v.CoM
                     );
                    break;
                case ReferenceFrame.Maneuver:
                    up = mVessel.transform.up;
                    if (mCurrentCommand.ManeuverCommand != null)
                    {
                        forward = mCurrentCommand.ManeuverCommand.Node.GetBurnVector(mVessel.orbit);
                        up = (v.mainBody.position - v.CoM);
                    }
                    else
                    {
                        forward = v.GetObtVelocity();
                        up = (v.mainBody.position - v.CoM);
                    }
                    break;
                case ReferenceFrame.TargetVelocity:
                    if (mCurrentCommand.TargetCommand != null && mCurrentCommand.TargetCommand.Target is Vessel)
                    {
                        forward = v.GetObtVelocity() - mCurrentCommand.TargetCommand.Target.GetObtVelocity();
                        up = (v.mainBody.position - v.CoM);
                    }
                    else
                    {
                        up = (v.mainBody.position - v.CoM);
                        forward = v.GetObtVelocity();
                    }
                    break;
                case ReferenceFrame.TargetParallel:
                    if (mCurrentCommand.TargetCommand != null)
                    {
                        forward = mCurrentCommand.TargetCommand.Target.GetTransform().position - v.CoM;
                        up = (v.mainBody.position - v.CoM);
                    }
                    else
                    {
                        up = (v.mainBody.position - v.CoM);
                        forward = v.GetObtVelocity();
                    }
                    break;
            }
            Vector3.OrthoNormalize(ref forward, ref up);
            rotationReference = Quaternion.LookRotation(forward, up);
            switch (mCurrentCommand.AttitudeCommand.Attitude)
            {
                case FlightAttitude.Prograde:
                    break;
                case FlightAttitude.Retrograde:
                    rotationReference = rotationReference * Quaternion.AngleAxis(180, Vector3.up);
                    break;
                case FlightAttitude.NormalPlus:
                    rotationReference = rotationReference * Quaternion.AngleAxis(90, Vector3.up);
                    break;
                case FlightAttitude.NormalMinus:
                    rotationReference = rotationReference * Quaternion.AngleAxis(90, Vector3.down);
                    break;
                case FlightAttitude.RadialPlus:
                    rotationReference = rotationReference * Quaternion.AngleAxis(90, Vector3.right);
                    break;
                case FlightAttitude.RadialMinus:
                    rotationReference = rotationReference * Quaternion.AngleAxis(90, Vector3.left);
                    break;
                case FlightAttitude.Surface:
                    rotationReference = rotationReference * mCurrentCommand.AttitudeCommand.Orientation;
                    break;
            }
            HoldOrientation(fs, rotationReference);
        }

        private void OnFlyByWirePre(FlightCtrlState fcs)
        {
            if (!mParent.IsMaster) return;
            var satellite = RTCore.Instance.Satellites[mParent.Guid];

            if (mVessel == FlightGlobals.ActiveVessel && InputAllowed && !satellite.HasLocalControl)
            {
                Enqueue(fcs);
            }

            if (!satellite.HasLocalControl)
            {
                PopFlightCtrlState(fcs, satellite);
            }

            mPreviousFcs.CopyFrom(fcs);
        }

        private void OnFlyByWirePost(FlightCtrlState fcs)
        {
            if (!mParent.IsMaster) return;

            if (!InputAllowed)
            {
                fcs.Neutralize();
            }

            Autopilot(fcs);

            foreach (var pilot in SanctionedPilots)
            {
                pilot.Invoke(fcs);
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public IEnumerator<DelayedCommand> GetEnumerator()
        {
            yield return mCurrentCommand;
            foreach (DelayedCommand dc in mCommandBuffer)
            {
                yield return dc;
            }
        }
    }
}

﻿// © Customize+.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CustomizePlus.Data.Profile;
using CustomizePlus.Extensions;
using CustomizePlus.Helpers;
using Dalamud.Logging;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.Havok;

namespace CustomizePlus.Data.Armature
{
    /// <summary>
    /// Represents a "copy" of the ingame skeleton upon which the linked character profile is meant to operate.
    /// Acts as an interface by which the in-game skeleton can be manipulated on a bone-by-bone basis.
    /// </summary>
    public unsafe class Armature
    {
        /// <summary>
        /// Gets the Customize+ profile for which this mockup applies transformations.
        /// </summary>
        public CharacterProfile Profile { get; init; }

        /// <summary>
        /// Gets or sets a value indicating whether or not this armature has any renderable objects on which it should act.
        /// </summary>
        public bool IsVisible { get; set; }

        /// <summary>
        /// Gets a value indicating whether or not this armature has successfully built itself with bone information.
        /// </summary>
        public bool IsBuilt { get; private set; }

        /// <summary>
        /// For debugging purposes, each armature is assigned a globally-unique ID number upon creation.
        /// </summary>
        private static uint _nextGlobalId;
        private readonly uint _localId;

        /// <summary>
        /// Each skeleton is made up of several smaller "partial" skeletons.
        /// Each partial skeleton has its own list of bones, with a root bone at index zero.
        /// The root bone of a partial skeleton may also be a regular bone in a different partial skeleton.
        /// </summary>
        private ModelBone[][] _partialSkeletons;

        #region Bone Accessors -------------------------------------------------------------------------------

        /// <summary>
        /// Gets the number of partial skeletons contained in this armature.
        /// </summary>
        public int PartialSkeletonCount => _partialSkeletons.Length;

        /// <summary>
        /// Get the list of bones belonging to the partial skeleton at the given index.
        /// </summary>
        public ModelBone[] this[int i]
        {
            get => _partialSkeletons[i];
        }

        /// <summary>
        /// Returns the number of bones contained within the partial skeleton with the given index.
        /// </summary>
        public int GetBoneCountOfPartial(int partialIndex) => _partialSkeletons[partialIndex].Length;

        /// <summary>
        /// Get the bone at index 'j' within the partial skeleton at index 'i'.
        /// </summary>
        public ModelBone this[int i, int j]
        {
            get => _partialSkeletons[i][j];
        }

        /// <summary>
        /// Returns the root bone of the partial skeleton with the given index.
        /// </summary>
        public ModelBone GetRootBoneOfPartial(int partialIndex) => this[partialIndex, 0];

        public ModelBone MainRootBone => GetRootBoneOfPartial(0);

        /// <summary>
        /// Get the total number of bones in each partial skeleton combined.
        /// </summary>
        // In exactly one partial skeleton will the root bone be an independent bone. In all others, it's a reference to a separate, real bone.
        // For that reason we must subtract the number of duplicate bones
        public int TotalBoneCount => _partialSkeletons.Sum(x => x.Length) - (_partialSkeletons.Length - 1);

        public IEnumerable<ModelBone> GetAllBones()
        {
            for (int i = 0; i < _partialSkeletons.Length; ++i)
            {
                for (int j = 0; j < _partialSkeletons[i].Length; ++j)
                {
                    yield return this[i, j];
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether this armature has yet built its skeleton.
        /// </summary>
        public bool Built => _partialSkeletons.Any();

        //----------------------------------------------------------------------------------------------------
        #endregion

        /// <summary>
        /// Gets or sets a value indicating whether or not this armature should snap all of its bones to their reference "bindposes".
        /// i.e. force the character ingame to assume their "default" pose.
        /// </summary>
        public bool SnapToReferencePose
        {
            get => GetReferenceSnap();
            set => SetReferenceSnap(value);
        }
        private bool _snapToReference;

        public Armature(CharacterProfile prof)
        {
            _localId = _nextGlobalId++;

            _partialSkeletons = Array.Empty<ModelBone[]>();

            Profile = prof;
            IsVisible = false;

            //cross-link the two, though I'm not positive the profile ever needs to refer back
            Profile.Armature = this;

            TryLinkSkeleton();

            PluginLog.LogDebug($"Instantiated {this}, attached to {Profile}");

        }

        /// <summary>
        /// Returns whether or not this armature was designed to apply to an object with the given name.
        /// </summary>
        public bool AppliesTo(string objectName) => Profile.AppliesTo(objectName);

        /// <inheritdoc/>
        public override string ToString()
        {
            return Built
                ? $"Armature (#{_localId}) on {Profile.CharacterName} with {TotalBoneCount} bone/s"
                : $"Armature (#{_localId}) on {Profile.CharacterName} with no skeleton reference";
        }

        private bool GetReferenceSnap()
        {
            if (Profile != Plugin.ProfileManager.ProfileOpenInEditor)
                _snapToReference = false;

            return _snapToReference;
        }

        private void SetReferenceSnap(bool value)
        {
            if (value && Profile == Plugin.ProfileManager.ProfileOpenInEditor)
                _snapToReference = false;

            _snapToReference = value;
        }

        /// <summary>
        /// Returns whether or not a link can be established between the armature and an in-game object.
        /// If unbuilt, the armature will use this opportunity to rebuild itself.
        /// </summary>
        public bool TryLinkSkeleton(bool forceRebuild = false)
        {
            try
            {
                if (DalamudServices.ObjectTable.FirstOrDefault(Profile.AppliesTo) is GameObject obj
                    && obj != null)
                {
                    if (!Built || forceRebuild)
                    {
                        RebuildSkeleton(obj.ToCharacterBase());
                    }
                    return true;
                }
            }
            catch
            {
                PluginLog.LogError($"Error occured while attempting to link skeleton: {this}");
            }

            return false;
        }

        /// <summary>
        /// Rebuild the armature using the provided character base as a reference.
        /// </summary>
        public void RebuildSkeleton(CharacterBase* cbase)
        {
            if (cbase == null) 
                return;

            //List<List<ModelBone?>> newPartials = _partialSkeletons.Select(x => x.ToList()).ToList();

            //for now, let's just rebuild the whole thing
            //if that causes a performance problem we'll cross that bridge when we get to it

            List<List<ModelBone>> newPartials = new();

            try
            {
                //build the skeleton
                for (var pSkeleIndex = 0; pSkeleIndex < cbase->Skeleton->PartialSkeletonCount; ++pSkeleIndex)
                {
                    PartialSkeleton currentPartial = cbase->Skeleton->PartialSkeletons[pSkeleIndex];
                    hkaPose* currentPose = currentPartial.GetHavokPose(Constants.TruePoseIndex);

                    newPartials.Add(new());

                    if (currentPose == null)
                        continue;

                    for (var boneIndex = 0; boneIndex < currentPose->Skeleton->Bones.Length; ++boneIndex)
                    {
                        //bone index zero is always the root bone
                        //check to see if it's THE root bone, or just a reference to another partial
                        //all partials link back to partial 0, it turns out, so we can just base it on that

                        if (boneIndex == 0 && pSkeleIndex != 0)
                        {
                            //no need to copy the bone when we can just reference it directly
                            newPartials.Last().Add(newPartials[0][currentPartial.ConnectedParentBoneIndex]);
                        }
                        else if (currentPose->Skeleton->Bones[boneIndex].Name.String is string boneName &&
                            boneName != null)
                        {
                            //time to build a new bone
                            ModelBone newBone = new(this, boneName, pSkeleIndex, boneIndex);

                            //if (currentPose->Skeleton->ParentIndices[boneIndex] is short parentIndex
                            //    && parentIndex >= 0)
                            //{
                            //    newBone.AddParent(0, parentIndex);
                            //}

                            if (Profile.Bones.TryGetValue(boneName, out BoneTransform? bt)
                                && bt != null)
                            {
                                newBone.UpdateModel(bt);
                            }

                            newPartials.Last().Add(newBone);
                        }
                        else
                        {
                            //I don't THINK this should happen? but the way the skeletons are constructed
                            //it seems possible that there could be "empty" space between actual bones?

                            ModelBone newBone = new ModelBone(this, "N/A", pSkeleIndex, boneIndex);
                            PluginLog.LogDebug($"Encountered errant bone {newBone} while building {this}");
                            newPartials.Last().Add(newBone);
                        }
                    }
                }

                _partialSkeletons = newPartials.Select(x => x.ToArray()).ToArray();

                BoneData.LogNewBones(GetAllBones().Select(x => x.BoneName).ToArray());

                PluginLog.LogDebug($"Rebuilt {this}:");
            }
            catch (Exception ex)
            {
                PluginLog.LogError($"Error rebuilding armature skeleton: {ex}");
            }
        }

        public void UpdateBoneTransform(int partialIdx, int boneIdx, BoneTransform bt, bool mirror = false, bool propagate = false)
        {
            this[partialIdx, boneIdx].UpdateModel(bt, mirror, propagate);
        }

        public void ApplyTransformation(CharacterBase* cBase)
        {
            if (cBase != null)
            {
                foreach (ModelBone mb in GetAllBones())
                {
                    mb.ApplyModelTransform(cBase);
                }
            }
        }

        //public void OverrideWithReferencePose()
        //{
        //    for (var pSkeleIndex = 0; pSkeleIndex < Skeleton->PartialSkeletonCount; ++pSkeleIndex)
        //    {
        //        for (var poseIndex = 0; poseIndex < 4; ++poseIndex)
        //        {
        //            var snapPose = Skeleton->PartialSkeletons[pSkeleIndex].GetHavokPose(poseIndex);

        //            if (snapPose != null)
        //            {
        //                snapPose->SetToReferencePose();
        //            }
        //        }
        //    }
        //}

        //public void OverrideRootParenting()
        //{
        //    var pSkeleNot = Skeleton->PartialSkeletons[0];

        //    for (var pSkeleIndex = 1; pSkeleIndex < Skeleton->PartialSkeletonCount; ++pSkeleIndex)
        //    {
        //        var partialSkele = Skeleton->PartialSkeletons[pSkeleIndex];

        //        for (var poseIndex = 0; poseIndex < 4; ++poseIndex)
        //        {
        //            var currentPose = partialSkele.GetHavokPose(poseIndex);

        //            if (currentPose != null && partialSkele.ConnectedBoneIndex >= 0)
        //            {
        //                int boneIdx = partialSkele.ConnectedBoneIndex;
        //                int parentBoneIdx = partialSkele.ConnectedParentBoneIndex;

        //                var transA = currentPose->AccessBoneModelSpace(boneIdx, 0);
        //                var transB = pSkeleNot.GetHavokPose(0)->AccessBoneModelSpace(parentBoneIdx, 0);

        //                //currentPose->AccessBoneModelSpace(parentBoneIdx, hkaPose.PropagateOrNot.DontPropagate);

        //                for (var i = 0; i < currentPose->Skeleton->Bones.Length; ++i)
        //                {
        //                    currentPose->ModelPose[i] = ApplyPropagatedTransform(currentPose->ModelPose[i], transB,
        //                        transA->Translation, transB->Rotation);
        //                    currentPose->ModelPose[i] = ApplyPropagatedTransform(currentPose->ModelPose[i], transB,
        //                        transB->Translation, transA->Rotation);
        //                }
        //            }
        //        }
        //    }
        //}

        //private hkQsTransformf ApplyPropagatedTransform(hkQsTransformf init, hkQsTransformf* propTrans,
        //    hkVector4f initialPos, hkQuaternionf initialRot)
        //{
        //    var sourcePosition = propTrans->Translation.GetAsNumericsVector().RemoveWTerm();
        //    var deltaRot = propTrans->Rotation.ToQuaternion() / initialRot.ToQuaternion();
        //    var deltaPos = sourcePosition - initialPos.GetAsNumericsVector().RemoveWTerm();

        //    hkQsTransformf output = new()
        //    {
        //        Translation = Vector3
        //            .Transform(init.Translation.GetAsNumericsVector().RemoveWTerm() - sourcePosition, deltaRot)
        //            .ToHavokTranslation(),
        //        Rotation = deltaRot.ToHavokRotation(),
        //        Scale = init.Scale
        //    };

        //    return output;
        //}
    }
}
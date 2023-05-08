﻿// © Customize+.
// Licensed under the MIT license.

using CustomizePlus.Data.Configuration.Interfaces;
using CustomizePlus.Data.Profile;
using Dalamud.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace CustomizePlus.Data.Configuration.Version0
{
	internal class Version0Configuration : IPluginConfiguration, ILegacyConfiguration
	{
		public int Version { get; set; } = 0;
		public List<Version0BodyScale> BodyScales { get; set; } = new();
		public bool Enable { get; set; } = true;
		public bool AutomaticEditMode { get; set; } = false;

		public bool ApplyToNpcs { get; set; } = true;
		public bool ApplyToNpcsInCutscenes { get; set; } = true;
		public bool DebuggingMode { get; set; } = false;

		public ILegacyConfiguration? LoadFromFile(string path)
		{
			if (!Path.Exists(path))
				throw new ArgumentException("Specified config path is invalid");

			return JsonConvert.DeserializeObject<Version0Configuration>(File.ReadAllText(path));
		}

		public PluginConfiguration ConvertToLatestVersion()
		{
			PluginConfiguration config = new PluginConfiguration()
			{
				Version = PluginConfiguration.CurrentVersion,
				PluginEnabled = this.Enable,
				DebuggingMode = this.DebuggingMode,
				ApplytoNPCs = this.ApplyToNpcs,
				ApplytoNPCsInCutscenes = this.ApplyToNpcsInCutscenes
			};

			foreach (var bodyScale in BodyScales)
			{
				var newProfile = ProfileConverter.ConvertFromConfigV0(bodyScale);

				ProfileManager.ConvertedProfiles.Add(newProfile);
			}

			return config;
		}
	}
}

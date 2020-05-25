﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Volo.Abp.Cli.Args;
using Volo.Abp.DependencyInjection;

namespace Volo.Abp.Cli.Commands
{
    public class TranslateCommand: IConsoleCommand, ITransientDependency
    {
        public ILogger<TranslateCommand> Logger { get; set; }

        public Task ExecuteAsync(CommandLineArgs commandLineArgs)
        {
            var currentDirectory = @"D:\Github\volo\abp\modules\identity"; //Directory.GetCurrentDirectory();

            var targetCulture = commandLineArgs.Options.GetOrNull(Options.Culture.Short, Options.Culture.Long);
            if (targetCulture == null)
            {
                throw new CliUsageException(
                    "Target culture is missing!" +
                    Environment.NewLine + Environment.NewLine +
                    GetUsageInfo()
                );
            }

            var referenceCulture = commandLineArgs.Options.GetOrNull(Options.ReferenceCulture.Short, Options.ReferenceCulture.Long);
            if (referenceCulture == null)
            {
                throw new CliUsageException(
                    "reference culture is missing!" +
                    Environment.NewLine + Environment.NewLine +
                    GetUsageInfo()
                );
            }

            var outputFile = Path.Combine(currentDirectory,
                commandLineArgs.Options.GetOrNull(Options.Output.Short, Options.Output.Long)
                ?? "abp-translation.json");

            var allValues = Convert.ToBoolean(commandLineArgs.Options.GetOrNull(Options.AllValues.Short, Options.AllValues.Long)
                                              ?? "false");

            Logger.LogInformation("Abp Translate...");
            Logger.LogInformation("Target culture: " + targetCulture);
            Logger.LogInformation("Reference culture: " + referenceCulture);
            Logger.LogInformation("Output file: " + outputFile);
            if (allValues)
            {
                Logger.LogInformation("Include all keys");
            }

            var translateInfo = GetAbpTranslateInfo(currentDirectory, targetCulture, referenceCulture, allValues);

            File.WriteAllText(outputFile, JsonConvert.SerializeObject(translateInfo, Formatting.Indented));

            Logger.LogInformation($"The translation file has been created to {outputFile}.");

            return Task.CompletedTask;
        }

        private static AbpTranslateInfo GetAbpTranslateInfo(string directory, string targetCultureName, string referenceCultureName, bool allValues)
        {
            var translateInfo = new AbpTranslateInfo
            {
                ReferenceCulture = referenceCultureName,
                TargetCulture = targetCultureName,
                Resources = new List<AbpTranslateResource>()
            };

            var referenceCultureFiles = GetCultureJsonFiles(directory, referenceCultureName);
            foreach (var filePath in referenceCultureFiles)
            {
                var directoryName = Path.GetDirectoryName(filePath) ?? string.Empty;

                var referenceLocalizationInfo = GetAbpLocalizationInfoOrNull(filePath);
                if (referenceLocalizationInfo == null) // Not abp json file
                {
                    continue;
                }

                var resource = new AbpTranslateResource
                {
                    ResourcePath = directoryName,
                    Texts = new List<AbpTranslateResourceText>()
                };

                foreach (var text in referenceLocalizationInfo.Texts)
                {
                    resource.Texts.Add(new AbpTranslateResourceText
                    {
                        LocalizationKey = text.Name,
                        Reference = text.Value,
                        Target = string.Empty
                    });
                }

                //Use target json file content to fill resource texts
                var targetFile = Path.Combine(directoryName, $"{targetCultureName}.json");
                if (File.Exists(targetFile))
                {
                    var targetLocalizationInfo = GetAbpLocalizationInfoOrNull(targetFile);
                    foreach (var referenceResourceText in resource.Texts)
                    {
                        var text = targetLocalizationInfo.Texts.FirstOrDefault(x => x.Name == referenceResourceText.LocalizationKey);
                        referenceResourceText.Target = text?.Value ?? string.Empty;
                    }
                }

                if (!allValues)
                {
                    //Only include missing keys.
                    resource.Texts.RemoveAll(x => !x.Target.Equals(string.Empty));
                }

                if (resource.Texts.Any())
                {
                    translateInfo.Resources.Add(resource);
                }
            }

            return translateInfo;
        }

        private static IEnumerable<string> GetCultureJsonFiles(string directory,string cultureName)
        {
            var excludeDirectory = new List<string>()
            {
                "node_modules",
                Path.Combine("bin", "debug"),
                Path.Combine("obj", "debug")
            };

            var allCultureInfos = CultureInfo.GetCultures(CultureTypes.AllCultures);

            return Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories)
                .Where(file => excludeDirectory.All(x => file.IndexOf(x, StringComparison.OrdinalIgnoreCase) == -1))
                .Where(jsonFile => allCultureInfos.Any(culture => jsonFile.EndsWith($"{cultureName}.json", StringComparison.OrdinalIgnoreCase)));
        }

        private static AbpLocalizationInfo GetAbpLocalizationInfoOrNull(string path)
        {
            var json = File.ReadAllText(path);
            JObject jObject;
            try
            {
                jObject = JObject.Parse(json);
            }
            catch (Exception e)
            {
                return null;
            }

            var culture = jObject.GetValue("culture");
            var texts = jObject.GetValue("texts");
            if (culture == null || texts == null)
            {
                return null;
            }

            var localizationInfo = new AbpLocalizationInfo
            {
                Culture = culture.Value<string>(),
                Texts = new List<NameValue>()
            };

            if (texts.Any())
            {
                foreach (var text in texts)
                {
                    var property = (text as JProperty);
                    localizationInfo.Texts.Add(new NameValue(property?.Name, property?.Value.Value<string>()));
                }
            }

            return localizationInfo;
        }

        public string GetUsageInfo()
        {
            var sb = new StringBuilder();

            sb.AppendLine("");
            sb.AppendLine("Usage:");
            sb.AppendLine("");
            sb.AppendLine("  abp translate [options]");
            sb.AppendLine("");
            sb.AppendLine("Options:");
            sb.AppendLine("");
            sb.AppendLine("--culture|-c <culture>                       Target culture. eg: zh-Hans");
            sb.AppendLine("--reference-culture|-r <culture>             Default: en)");
            sb.AppendLine("--output|-o <file-name>                      Output file name");
            sb.AppendLine("--all-values|-all                            Include all keys");
            sb.AppendLine("--apply|-a                                   Creates or updates the file for the translated culture.");
            sb.AppendLine("--file|-f <file-name>                        Default: abp-translation.json");
            sb.AppendLine("");
            sb.AppendLine("Examples:");
            sb.AppendLine("");
            sb.AppendLine("  abp translate -c zh-Hans -r en");
            sb.AppendLine("  abp translate -c zh-Hans -r en -a");
            sb.AppendLine("  abp translate apply");
            sb.AppendLine("  abp translate apply -f my-translation.json");
            sb.AppendLine("");
            sb.AppendLine("See the documentation for more info: https://docs.abp.io/en/abp/latest/CLI");

            return sb.ToString();
        }

        public string GetShortDescription()
        {
            return "Mainly used to translate ABP's resources (JSON files) easier.";
        }

        public static class Options
        {
            public static class Culture
            {
                public const string Short = "c";
                public const string Long = "culture";
            }

            public static class ReferenceCulture
            {
                public const string Short = "r";
                public const string Long = "reference-culture";
            }

            public static class Output
            {
                public const string Short = "o";
                public const string Long = "output";
            }

            public static class AllValues
            {
                public const string Short = "all";
                public const string Long = "all-values";
            }

            public static class Apply
            {
                public const string Short = "a";
                public const string Long = "apply";
            }

            public static class File
            {
                public const string Short = "f";
                public const string Long = "file";
            }
        }

        public class AbpTranslateInfo
        {
            public string ReferenceCulture { get; set; }

            public string TargetCulture { get; set; }

            public List<AbpTranslateResource> Resources { get; set; }
        }

        public class AbpTranslateResource
        {
            public string ResourcePath { get; set; }

            public List<AbpTranslateResourceText> Texts { get; set; }
        }

        public class AbpTranslateResourceText
        {
            public string LocalizationKey { get; set; }

            public string Reference { get; set; }

            public string Target { get; set; }
        }

        public class AbpLocalizationInfo
        {
            public string Culture { get; set; }

            public List<NameValue> Texts { get; set; }
        }
    }
}

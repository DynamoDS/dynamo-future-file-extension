using Dynamo.Extensions;
using Dynamo.Graph.Nodes;
using Dynamo.Models;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Xml;
using Dynamo.Logging;
using System.Diagnostics;

namespace Dynamo.FutureFileExtension
{
    public class FutureFileExtension : IExtension, ILogSource
    {
        private static MethodInfo openxmlmethod;
        private static MethodInfo openJsonFileMethod;

        public event Action<ILogMessage> MessageLogged;

        public string UniqueId => "277edc98-d327-404e-979c-7c6e6f6956b2";

        public string Name => "FutureFileExtension_File_Compatability";

        public void Dispose()
        {

        }

        private static string ToEnumString<T>(T type)
        {
            var enumType = typeof(T);
            var name = Enum.GetName(enumType, type);
            var enumMemberAttribute = ((EnumMemberAttribute[])enumType.GetField(name).GetCustomAttributes(typeof(EnumMemberAttribute), true)).Single();
            return enumMemberAttribute.Value;
        }

        public void Ready(ReadyParams sp)
        {
            //Debugger.Launch();
            //if dynamo is newer than 2.11, don't monkey patch anything.
            if (sp.StartupParams.DynamoVersion > new Version(2, 12, 0))
            {
                MessageLogged(LogMessage.Info($"This extension only patches dynamo versions older than 2.12, current version is {Assembly.GetCallingAssembly().GetName().Version}, returning."));
                return;
            }

            DoPatching();
        }

        public void DoPatching()
        {
            var harmony = new Harmony("Dynamo.FurureFileExtension.Patch.1");

            var originalOpenFileFromPath = AccessTools.Method(typeof(DynamoModel), nameof(DynamoModel.OpenFileFromPath));
            var prefix = typeof(FutureFileExtension).GetMethod(nameof(Prefix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            openxmlmethod = typeof(DynamoModel).GetMethod("OpenXmlFileFromPath", System.Reflection.BindingFlags.NonPublic | BindingFlags.Instance);
            openJsonFileMethod = typeof(DynamoModel).GetMethod("OpenJsonFileFromPath", System.Reflection.BindingFlags.NonPublic | BindingFlags.Instance);
            if(originalOpenFileFromPath != null && prefix != null && openxmlmethod != null && openJsonFileMethod != null)
            {
                harmony.Patch(originalOpenFileFromPath, new HarmonyMethod(prefix));
            }
        }
        public static bool Prefix(DynamoModel __instance, string filePath, bool forceManualExecutionMode = false)
        {

            __instance?.Logger?.Log($"openfilepath has been patched from{Assembly.GetExecutingAssembly().FullName }");
            var knownInputTypeNames = Enum.GetValues(typeof(NodeInputTypes)).Cast<NodeInputTypes>().Select(x => ToEnumString(x));
            XmlDocument xmlDoc;
            Exception ex;
            if (DynamoUtilities.PathHelper.isValidXML(filePath, out xmlDoc, out ex))
            {
                openxmlmethod.Invoke(__instance, new object[] { xmlDoc, filePath, forceManualExecutionMode });
                return false;
            }
            else
            {
                // These kind of exceptions indicate that file is not accessible 
                if (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw ex;
                }
                if (ex is System.Xml.XmlException)
                {
                    // XML opening failure can indicate that this file is corrupted XML or Json
                    string fileContents;
                    if (DynamoUtilities.PathHelper.isValidJson(filePath, out fileContents, out ex))
                    {
                        //at this point filecontents contains a json string - 
                        //lets parse it, do our transformation and pass it to open method.

                        // TODO we could do more complex replacements, for example, we could replace the WorkspaceConverter.ReadJson method 
                        // and completely alter how nodeInputs are deserialized. This is just a POC to fix the issue with minimum effort.

                        var root = JObject.Parse(fileContents);
                        //find all inputs that don't exist in the known types - these are the ones we can't deserialize correctly.
                        //TODO reason about case sensitivity.
                        var matchingTokens = root.SelectTokens("Inputs[*].Type").Where(x => !knownInputTypeNames.Contains(x.Value<string>()));
                        //for now we just replace with "Selection" - but we could avoid deserializing this entire input.
                        matchingTokens.ToList().ForEach(x => x.Replace(JToken.FromObject("Selection")));
                        fileContents = root.ToString();

                        openJsonFileMethod.Invoke(__instance, new object[] { fileContents, filePath, forceManualExecutionMode });
                        return false;
                    }
                    else
                    {
                        throw ex;
                    }
                }
            }
            return false;
        }

        public void Shutdown()
        {

        }

        public void Startup(StartupParams sp)
        {

        }
    }
}

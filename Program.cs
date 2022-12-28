using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;

namespace NetLimiter.AutoPatch
{
    class Program
    {
        [DllImport("User32.dll", EntryPoint = "MessageBox", CharSet = CharSet.Auto)]
        static extern int MB(IntPtr hWnd, string lpText, string lpCaption, uint uType);

        static void MB(string text)
        {
            MB(IntPtr.Zero, text, "NetLimiter.AutoPatch", 0); /* MB_OK */
        }

        static bool IsAdministrator()
        {
            try
            {
                WindowsIdentity user = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(user);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch 
            {
                return false;
            }
        }

        static bool FindInstallLocation(out string path, out int version)
        {
            var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Locktime Software\NetLimiter 4");

            if (key != null)
            {
                path = (string)key.GetValue("Path");
                version = 4;
                return true;
            }

            // newer versions of NL4, and NL5 - unified registry path
            key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Locktime Software\NetLimiter");

            if (key != null)
            {
                path = (string)key.GetValue("Path");
                version = ((string)key.GetValue("Version")).StartsWith("4") ? 4 : 5;
                return true;
            }

            path = "";
            version = 0;
            return false;
        }

        static void StopNetLimiter(ServiceController service)
        {
            // exit app
            var procs = Process.GetProcessesByName("NLClientApp");

            if (procs.Length > 0)
            {
                Console.WriteLine("Stopping NetLimiter application...");

                foreach (var proc in procs)
                {
                    proc.Kill();
                }

                Console.WriteLine("NetLimiter application stopped!");
            }

            // stop service
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                Console.WriteLine("Stopping NetLimiter service...");
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped);
                Console.WriteLine("NetLimiter service stopped!");
            }
        }

        static void StartNetLimiter(ServiceController service)
        {
            Console.WriteLine("Starting NetLimiter service...");
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running);
            Console.WriteLine("NetLimiter service started!");
        }

        static Dictionary<string, List<Instruction>> patches_all = new()
        {
            {
                "get_IsRegistered", new()
                {
                    new Instruction(OpCodes.Ldc_I4_1),
                    new Instruction(OpCodes.Ret)
                }
            },
            {
                "get_HasExpiration", new()
                {
                    new Instruction(OpCodes.Ldc_I4_1),
                    new Instruction(OpCodes.Ret)
                }
            },
            {
                "get_IsExpired", new()
                {
                    new Instruction(OpCodes.Ldc_I4_1),
                    new Instruction(OpCodes.Ret)
                }
            },
            {
                "get_ProductCode", new()
                {
                    new Instruction(OpCodes.Ldstr, "valid product code ;)"),
                    new Instruction(OpCodes.Ret)
                }
            },
            {
                "get_Quantity", new()
                {
                    new Instruction(OpCodes.Ldc_I4, 420),
                    new Instruction(OpCodes.Ret)
                }
            }
        };

        static Dictionary<string, List<Instruction>> patches_v4 = new()
        {
            {
                "get_RegistrationName", new()
                {
                    new Instruction(OpCodes.Ldstr, Environment.UserName),
                    new Instruction(OpCodes.Ret)
                }
            }
        };

        static Dictionary<string, List<Instruction>> patches_v5 = new()
        {
            {
                "get_RegName", new()
                {
                    new Instruction(OpCodes.Ldstr, Environment.UserName),
                    new Instruction(OpCodes.Ret)
                }
            },
            {
                "get_ExtendedDaysLeft", new()
                {
                    new Instruction(OpCodes.Ldc_I4, 69),
                    new Instruction(OpCodes.Ret)
                }
            },
            {
                "get_DaysLeftRaw", new()
                {
                    new Instruction(OpCodes.Ldc_I4, 69),
                    new Instruction(OpCodes.Ret)
                }
            },
            {
                "get_PlanId", new()
                {
                    new Instruction(OpCodes.Ldstr, "valid plan ;)"),
                    new Instruction(OpCodes.Ret)
                }
            }
        };

        static void PerformPatches(TypeDef type, Dictionary<string, List<Instruction>> patches)
        {
            foreach (var kvp in patches)
            {
                // `foreach (var (methodName, patch) in patches)`
                var methodName = kvp.Key;
                var patch = kvp.Value;

                var method = type.Methods.FirstOrDefault(m => m.Name == methodName);

                if (method != null)
                {
                    method.Body.Instructions.Clear();

                    foreach (var p in patch)
                    {
                        method.Body.Instructions.Add(p);
                    }
                }
                else
                {
                    // log somewhere
                }
            }
        }

        static void PatchAssembly(string installDirectory, int version)
        {
            var targetPath = Path.Combine(installDirectory, "NetLimiter.dll");
            var backupPath = targetPath + ".backup";
            
            // delete previous backup if it exists
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            byte[] data = File.ReadAllBytes(targetPath);

            // back the file up
            File.Copy(targetPath, backupPath);

            ModuleContext modCtx = ModuleDef.CreateModuleContext();
            ModuleDefMD def = ModuleDefMD.Load(data, modCtx);

            Importer imp = new Importer(def);

            // valid license perks
            {
                TypeDef features = def.Types.FirstOrDefault(t => t.Name == "SupportedFeatures");

                var method = features.Methods.FirstOrDefault(m => m.Name == "IsSupported");

                method.Body.Instructions.Clear();

                method.Body.Instructions.Add(new Instruction(OpCodes.Ldc_I4_1));
                method.Body.Instructions.Add(new Instruction(OpCodes.Ret));
            }

            // license class
            TypeDef licenseType = def.Types.FirstOrDefault(t => t.Name == "NLLicense");

            // set license type
            {
                var method = licenseType.Methods.FirstOrDefault(m => m.Name == "get_LicenseType");

                method.Body.Instructions.Clear();

                if (version == 4)
                {
                    var lt = def.Types.FirstOrDefault(t => t.Name == "NLLicenseType");
                    var field = lt.Fields.FirstOrDefault(f => f.Name == "Enterprice");

                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldc_I4, lt.Fields.IndexOf(field) - 1));
                }
                else
                {
                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldstr, "Enterprise"));
                    method.Body.Instructions.Add(new Instruction(OpCodes.Ret));
                }
            }

            PerformPatches(licenseType, patches_all);
            PerformPatches(licenseType, version == 4 ? patches_v4 : patches_v5);

            ModuleWriterOptions moduleWriterOptions = new ModuleWriterOptions(def)
            {
                Logger = DummyLogger.NoThrowInstance
            };

            def.Write(targetPath, moduleWriterOptions);
        }

        public static void Main(string[] args)
        {
            if (!IsAdministrator())
            {
                MB("Error: This application must be run with Administrator privileges.");
                Environment.Exit(1);
            }

            bool usingCustomPath = false;
            string customLocation = "";

            if (args.Length > 0)
            {
                var files = Directory.EnumerateFiles(args[0]);
                var file = files.FirstOrDefault(f => f.ToLowerInvariant().Contains("nlclientapp.exe"));

                if (file != null)
                {
                    customLocation = args[0];
                    usingCustomPath = true;
                }
                else
                {
                    MB("Error: Selected directory is not a valid NetLimiter installation.");
                }
            }
            
            if (FindInstallLocation(out var location, out var version) || usingCustomPath)
            {
                var svc = new ServiceController("nlsvc");
                StopNetLimiter(svc);
                PatchAssembly(usingCustomPath ? customLocation : location, version);
                StartNetLimiter(svc);
            }
            else
            {
                MB("NetLimiter installation location not found.");
                Environment.Exit(1);
            }

            MB("Done! You can now start the NetLimiter application normally.");
        }
    }
}
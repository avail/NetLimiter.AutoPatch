using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using Microsoft.Win32;
using System;
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

        // Computer\HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Locktime Software\NetLimiter 4
        // field Path
        static bool FindInstallLocation(out string path)
        {
            var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Locktime Software\NetLimiter 4");

            if (key != null)
            {
                path = (string)key.GetValue("Path");
                return true;
            }

            path = "";
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

        static void PatchAssembly(string installDirectory)
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

            // license itself
            TypeDef licenseType = def.Types.FirstOrDefault(t => t.Name == "NLLicense");

            // lazy loop!
            licenseType.Methods.ToList().ForEach((method) =>
            {
                // can return true to testing version content as well
                // but it's usually buggier
                //method.Name == "get_IsTestingVersion"

                // return true
                if (method.Name == "get_IsRegistered")// is registered
                {
                    method.Body.Instructions.Clear();

                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldc_I4_1));
                    method.Body.Instructions.Add(new Instruction(OpCodes.Ret));
                }

                // return false
                if (method.Name == "get_HasExpiration" || // can expire?
                    method.Name == "get_IsExpired") // is expired
                {
                    method.Body.Instructions.Clear();

                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldc_I4_0));
                    method.Body.Instructions.Add(new Instruction(OpCodes.Ret));
                }

                // set product code
                if (method.Name == "get_ProductCode")
                {
                    method.Body.Instructions.Clear();

                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldstr, "valid product code ;)"));
                    method.Body.Instructions.Add(new Instruction(OpCodes.Ret));
                }

                // set licensee
                if (method.Name == "get_RegistrationName")
                {
                    method.Body.Instructions.Clear();

                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldstr, Environment.UserName));
                    method.Body.Instructions.Add(new Instruction(OpCodes.Ret));
                }

                // quantity
                if (method.Name == "get_Quantity")
                {
                    method.Body.Instructions.Clear();

                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldc_I4, 420));
                    method.Body.Instructions.Add(new Instruction(OpCodes.Ret));
                }

                // set license type to enterprise
                if (method.Name == "get_LicenseType")
                {
                    var lt = def.Types.FirstOrDefault(t => t.Name == "NLLicenseType");
                    var field = lt.Fields.FirstOrDefault(f => f.Name == "Enterprice");

                    method.Body.Instructions.Clear();

                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldc_I4, lt.Fields.IndexOf(field) - 1));
                    method.Body.Instructions.Add(new Instruction(OpCodes.Ret));
                }
            });

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

            if (FindInstallLocation(out var location))
            {
                var svc = new ServiceController("nlsvc");
                StopNetLimiter(svc);
                PatchAssembly(location);
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
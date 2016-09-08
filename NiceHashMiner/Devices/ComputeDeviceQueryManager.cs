﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using NiceHashMiner.Configs;
using NiceHashMiner.Interfaces;
using NiceHashMiner.Enums;
using NiceHashMiner.Miners;
using System.Diagnostics;
using Newtonsoft.Json;
using ATI.ADL;
using System.Runtime.InteropServices;
using System.Management;
using System.IO;
using System.Globalization;
using NiceHashMiner.Utils;

namespace NiceHashMiner.Devices
{
    /// <summary>
    /// ComputeDeviceQueryManager class is used to query ComputeDevices avaliable on the system.
    /// Query CPUs, GPUs [Nvidia, AMD]
    /// </summary>
    public class ComputeDeviceQueryManager : BaseLazySingleton<ComputeDeviceQueryManager>
    {

        const int AMD_VENDOR_ID = 1002;
        readonly string TAG;

        const double NVIDIA_RECOMENDED_DRIVER = 372.54;
        const double NVIDIA_MIN_DETECTION_DRIVER = 362.61;
        double _currentNvidiaOpenCLDriver = -1;
        JsonSerializerSettings _jsonSettings = null;
            
        protected ComputeDeviceQueryManager() {
            TAG = this.GetType().Name;
            _jsonSettings = new JsonSerializerSettings {
                NullValueHandling = NullValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                Culture = CultureInfo.InvariantCulture
            };
        }

        public int CPUs { get; private set; }

        public int AMDOpenCLPlatformNum { get; private set; }
        public string AMDOpenCLPlatformStringKey { get; private set; }

        public IMessageNotifier MessageNotifier { get; private set; }

        public void QueryDevices(IMessageNotifier messageNotifier)
        {
            MessageNotifier = messageNotifier;
            // #0 get video controllers, used for cross checking
            QueryVideoControllers();
            // Order important CPU Query must be first
            // #1 CPU
            QueryCPUs();
            // #2 CUDA
            showMessageAndStep("Querying CUDA devices");
            QueryCudaDevices();
            // #3 OpenCL
            showMessageAndStep("Querying OpenCL devices");
            QueryOpenCLDevices();
            // #4 AMD query AMD from OpenCL devices, get serial and add devices
            QueryAMD();
            // #5 uncheck CPU if GPUs present, call it after we Query all devices
            UncheckedCPU();
            // add numberings to same devices
            if (ComputeDevice.AllAvaliableDevices.Count != ComputeDevice.UniqueAvaliableDevices.Count) {
                // name count
                Dictionary<string, int> namesCount = new Dictionary<string, int>();
                // init keys and counters
                foreach (var uniqueCdev in ComputeDevice.UniqueAvaliableDevices) {
                    namesCount.Add(uniqueCdev.Name, 0);
                }
                // count 
                foreach (var cDev in ComputeDevice.AllAvaliableDevices) {
                    namesCount[cDev.Name]++;
                }
                foreach (var nameCount in namesCount) {
                    string name = nameCount.Key;
                    int deviceCount = nameCount.Value;
                    if (deviceCount > 1) {
                        int numID = 1;
                        foreach (var cDev in ComputeDevice.AllAvaliableDevices) {
                            if (cDev.Name == name) {
                                cDev.Name = cDev.Name + " #" + numID.ToString();
                                ++numID;
                            }
                        }
                    }
                }
            }


            // TODO update this to report undetected hardware
            // #6 check NVIDIA, AMD devices count
            {
                int NVIDIA_count = 0;
                int AMD_count = 0;
                foreach (var vidCtrl in AvaliableVideoControllers) {
                    NVIDIA_count += (vidCtrl.Name.ToLower().Contains("nvidia")) ? 1 : 0;
                    AMD_count += (vidCtrl.Name.ToLower().Contains("amd")) ? 1 : 0;
                }
                if (NVIDIA_count == CudaDevices.Count) {
                    Helpers.ConsolePrint(TAG, "Cuda NVIDIA/CUDA device count GOOD");
                } else {
                    Helpers.ConsolePrint(TAG, "Cuda NVIDIA/CUDA device count BAD!!!");
                }
                if (AMD_count == amdGpus.Count) {
                    Helpers.ConsolePrint(TAG, "AMD GPU device count GOOD");
                } else {
                    Helpers.ConsolePrint(TAG, "AMD GPU device count BAD!!!");
                }
            }
            // #7 init ethminer ID mappings offset
            if (OpenCLJSONData != null) {
                // helper vars
                Dictionary<ComputePlatformType, int> openCLGpuCount = new Dictionary<ComputePlatformType,int>();
                Dictionary<ComputePlatformType, int> openCLPlatformIds = new Dictionary<ComputePlatformType,int>();
                foreach (var oclPlatform in OpenCLJSONData.OCLPlatforms) {
                    ComputePlatformType current = GetPlatformType(oclPlatform.Key);
                    if(current != ComputePlatformType.NONE) {
                        openCLPlatformIds[current] = oclPlatform.Value;
                    } else {
                        Helpers.ConsolePrint(TAG, "ethminer platforms mapping NONE");
                    }
                }
                foreach (var oclDevs in OpenCLJSONData.OCLPlatformDevices) {
                    ComputePlatformType current = GetPlatformType(oclDevs.Key);
                    if (current != ComputePlatformType.NONE) {
                        foreach (var oclDev in oclDevs.Value) {
                            if (oclDev._CL_DEVICE_TYPE.Contains("GPU")) {
                                if (openCLGpuCount.ContainsKey(current)) {
                                    openCLGpuCount[current]++;
                                } else {
                                    openCLGpuCount[current] = 1;
                                }
                            }
                        }
                    } else {
                        Helpers.ConsolePrint(TAG, "ethminer platforms mapping NONE");
                    }
                }
                // sort platforms by platform values
                Dictionary<int, ComputePlatformType> openCLPlatformIdsRev = new Dictionary<int,ComputePlatformType>();
                List<int> platformIds = new List<int>();
                foreach (var platId in openCLPlatformIds) {
                    openCLPlatformIdsRev[platId.Value] = platId.Key;
                    platformIds.Add(platId.Value);
                }
                platformIds.Sort();
                // set mappings
                int cumulativeCount = 0;
                foreach (var curId in platformIds) {
                    var key = openCLPlatformIdsRev[curId];
                    if (openCLGpuCount.ContainsKey(key)) {
                        _ethminerIdsOffet[key] = cumulativeCount;
                        cumulativeCount += openCLGpuCount[key]; 
                    }
                }
            }
            // allerts
            _currentNvidiaOpenCLDriver = GetNvidiaOpenCLDriver();
            // if we have nvidia cards but no CUDA devices tell the user to upgrade driver
            if (HasNvidiaVideoController() && CudaDevices.Count == 0) {
                var minDriver = NVIDIA_MIN_DETECTION_DRIVER.ToString();
                var recomendDrvier = NVIDIA_RECOMENDED_DRIVER.ToString();
                MessageBox.Show(String.Format("We have detected that your system has Nvidia GPUs, but your driver is older then {0}. In order for NiceHash Miner to work correctly you should upgrade your drivers to recomended {1} or newer.",
                    minDriver, recomendDrvier),
                                                      "Nvidia Recomended driver",
                                                      MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            // recomended driver
            if (HasNvidiaVideoController() && _currentNvidiaOpenCLDriver < NVIDIA_RECOMENDED_DRIVER) {
                var recomendDrvier = NVIDIA_RECOMENDED_DRIVER.ToString();
                var nvdriverString = _currentNvidiaOpenCLDriver > -1 ? String.Format(" (current {0})", _currentNvidiaOpenCLDriver.ToString())
                : "";
                MessageBox.Show(String.Format("We have detected that your Nvidia Driver is older then {0}{1}. We recommend you to update to {2} or newer.",
                    recomendDrvier, nvdriverString, recomendDrvier),
                                                      "Nvidia Recomended driver",
                                                      MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // #x remove reference
            MessageNotifier = null;
        }

        private Dictionary<ComputePlatformType, int> _ethminerIdsOffet = new Dictionary<ComputePlatformType,int>();
        public int GetEthminerOpenCLID(ComputePlatformType platformType, int id) {
            return _ethminerIdsOffet[platformType] + id;
        }

        private ComputePlatformType GetPlatformType(string name) {
            if (name.Contains("Intel")) {
                return ComputePlatformType.Intel;
            }
            if (name.Contains("AMD")) {
                return ComputePlatformType.AMD;
            }
            if (name.Contains("NVIDIA")) {
                return ComputePlatformType.NVIDIA;
            }
            return ComputePlatformType.NONE;
        }

        private void showMessageAndStep(string infoMsg) {
            if (MessageNotifier != null) MessageNotifier.SetMessageAndIncrementStep(infoMsg);
        }

        #region Video controllers, driver versions

        private class VideoControllerData {
            public string Name { get; set; }
            public string Description { get; set; }
            public string PNPDeviceID { get; set; }
            public string DriverVersion { get; set; }
        }

        private List<VideoControllerData> AvaliableVideoControllers = new List<VideoControllerData>();

        private void QueryVideoControllers() {
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("");
            stringBuilder.AppendLine("QueryVideoControllers: ");
            ManagementObjectCollection moc = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_VideoController").Get();
            foreach (var manObj in moc) {
                stringBuilder.AppendLine(String.Format("\tGPU Name (Driver Ver): {0} ({1})", manObj["Name"] , manObj["DriverVersion"]));
                AvaliableVideoControllers.Add(
                    new VideoControllerData() {
                        Name = manObj["Name"] as string,
                        Description = manObj["Description"] as string,
                        PNPDeviceID = manObj["PNPDeviceID"] as string,
                        DriverVersion = manObj["DriverVersion"] as string
                    });
            }
            Helpers.ConsolePrint(TAG, stringBuilder.ToString());
        }

        private bool HasNvidiaVideoController() {
            foreach (var vctrl in AvaliableVideoControllers) {
                if (vctrl.Name.ToLower().Contains("nvidia")) return true;
            }
            return false;
        }

        #endregion // Video controllers, driver versions

        private void QueryCPUs() {
            // get all CPUs
            CPUs = CPUID.GetPhysicalProcessorCount();

            // get all cores (including virtual - HT can benefit mining)
            int ThreadsPerCPU = CPUID.GetVirtualCoresCount() / CPUs;

            if (!Helpers.InternalCheckIsWow64())
            {
                MessageBox.Show(International.GetText("form1_msgbox_CPUMining64bitMsg"),
                                International.GetText("Warning_with_Exclamation"),
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                CPUs = 0;
            }

            if (ThreadsPerCPU * CPUs > 64)
            {
                MessageBox.Show(International.GetText("form1_msgbox_CPUMining64CoresMsg"),
                                International.GetText("Warning_with_Exclamation"),
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                CPUs = 0;
            }

            // TODO important move this to settings
            int ThreadsPerCPUMask = ThreadsPerCPU;
            Globals.ThreadsPerCPU = ThreadsPerCPU;
            
            if (CPUs == 1) {
                MinersManager.Instance.AddCpuMiner(new cpuminer(0, ThreadsPerCPU, 0), 0, CPUID.GetCPUName().Trim());
            }
            else {
                for (int i = 0; i < CPUs; i++) {
                    MinersManager.Instance.AddCpuMiner(new cpuminer(i, ThreadsPerCPU, CPUID.CreateAffinityMask(i, ThreadsPerCPUMask)),
                        i, CPUID.GetCPUName().Trim());
                }
            }
        }

        List<OpenCLDevice> amdGpus = new List<OpenCLDevice>();
        private void QueryAMD() {
            //showMessageAndStep(International.GetText("form1_loadtext_AMD"));
            //var dump = new sgminer(true);

            if(ConfigManager.Instance.GeneralConfig.DeviceDetection.DisableDetectionAMD) {
                Helpers.ConsolePrint(TAG, "Skipping AMD device detection, settings set to disabled");
                showMessageAndStep("Skip check for AMD OpenCL GPUs");
                return;
            }

            #region AMD driver check, ADL returns 0
            // check the driver version bool EnableOptimizedVersion = true;
            Dictionary<string, bool> deviceDriverOld = new Dictionary<string, bool>();
            string minerPath = MinerPaths.sgminer_5_4_0_general;
            bool ShowWarningDialog = false;

            foreach (var vidContrllr in AvaliableVideoControllers) {
                Helpers.ConsolePrint(TAG, String.Format("Checking AMD device (driver): {0} ({1})", vidContrllr.Name, vidContrllr.DriverVersion));

                deviceDriverOld[vidContrllr.Name] = false;
                // TODO checking radeon drivers only?
                if ((vidContrllr.Name.Contains("AMD") || vidContrllr.Name.Contains("Radeon")) && ShowWarningDialog == false) {
                    Version AMDDriverVersion = new Version(vidContrllr.DriverVersion);

                    if (AMDDriverVersion.Major < 15) {
                        ShowWarningDialog = true;
                        deviceDriverOld[vidContrllr.Name] = true;
                        Helpers.ConsolePrint(TAG, "WARNING!!! Old AMD GPU driver detected! All optimized versions disabled, mining " +
                            "speed will not be optimal. Consider upgrading AMD GPU driver. Recommended AMD GPU driver version is 15.7.1.");
                    } else if (AMDDriverVersion.Major == 16 && AMDDriverVersion.Minor >= 150) {
                        if (MinersDownloadManager.Instance.IsMinerBinFolder()) {
                            // TODO why this copy?
                            string src = System.IO.Path.GetDirectoryName(Application.ExecutablePath) + "\\" +
                                         minerPath.Split('\\')[0] + "\\" + minerPath.Split('\\')[1] + "\\kernel";

                            foreach (var file in Directory.GetFiles(src)) {
                                string dest = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Temp\\" + System.IO.Path.GetFileName(file);
                                if (!File.Exists(dest)) File.Copy(file, dest, false);
                            }
                        }
                    }
                }
            }
            if (ShowWarningDialog == true && ConfigManager.Instance.GeneralConfig.ShowDriverVersionWarning == true) {
                Form WarningDialog = new DriverVersionConfirmationDialog();
                WarningDialog.ShowDialog();
                WarningDialog = null;
            }
            #endregion // AMD driver check

            // get platform version
            showMessageAndStep("Checking AMD OpenCL GPUs");
            if (IsOpenCLQuerrySuccess) {
                bool amdPlatformNumFound = false;
                foreach (var kvp in OpenCLJSONData.OCLPlatforms) {
                    if (kvp.Key.Contains("AMD") || kvp.Key.Contains("amd")) {
                        amdPlatformNumFound = true;
                        AMDOpenCLPlatformStringKey = kvp.Key;
                        AMDOpenCLPlatformNum = kvp.Value;
                        Helpers.ConsolePrint(TAG, String.Format("AMD platform found: Key: {0}, Num: {1}",
                            AMDOpenCLPlatformStringKey,
                            AMDOpenCLPlatformNum.ToString()));
                        break;
                    }
                }
                if (amdPlatformNumFound) {
                    // get only AMD gpus
                    {
                        var amdOCLDevices = OpenCLJSONData.OCLPlatformDevices[AMDOpenCLPlatformStringKey];
                        foreach (var oclDev in amdOCLDevices) {
                            if (oclDev._CL_DEVICE_TYPE.Contains("GPU")) {
                                amdGpus.Add(oclDev);
                            } 
                        }
                    }
                    if (amdGpus.Count == 0) {
                        Helpers.ConsolePrint(TAG, "AMD GPUs count is 0");
                    } else {
                        Helpers.ConsolePrint(TAG, "AMD GPUs count : " + amdGpus.Count.ToString());
                        Helpers.ConsolePrint(TAG, "AMD Getting device name and serial from ADL");
                        // ADL
                        bool isAdlInit = true;
                        // ADL should get our devices in order
                        //HashSet<int> _busIds = new HashSet<int>();
                        List<string> _amdDeviceName = new List<string>();
                        List<string> _amdDeviceUUID = new List<string>();
                        try {
                            int ADLRet = -1;
                            int NumberOfAdapters = 0;
                            if (null != ADL.ADL_Main_Control_Create)
                                // Second parameter is 1: Get only the present adapters
                                ADLRet = ADL.ADL_Main_Control_Create(ADL.ADL_Main_Memory_Alloc, 1);
                            if (ADL.ADL_SUCCESS == ADLRet) {
                                if (null != ADL.ADL_Adapter_NumberOfAdapters_Get) {
                                    ADL.ADL_Adapter_NumberOfAdapters_Get(ref NumberOfAdapters);
                                }
                                Helpers.ConsolePrint(TAG, "Number Of Adapters: " + NumberOfAdapters.ToString());

                                if (0 < NumberOfAdapters) {
                                    // Get OS adpater info from ADL
                                    ADLAdapterInfoArray OSAdapterInfoData;
                                    OSAdapterInfoData = new ADLAdapterInfoArray();

                                    if (null != ADL.ADL_Adapter_AdapterInfo_Get) {
                                        IntPtr AdapterBuffer = IntPtr.Zero;
                                        int size = Marshal.SizeOf(OSAdapterInfoData);
                                        AdapterBuffer = Marshal.AllocCoTaskMem((int)size);
                                        Marshal.StructureToPtr(OSAdapterInfoData, AdapterBuffer, false);

                                        if (null != ADL.ADL_Adapter_AdapterInfo_Get) {
                                            ADLRet = ADL.ADL_Adapter_AdapterInfo_Get(AdapterBuffer, size);
                                            if (ADL.ADL_SUCCESS == ADLRet) {
                                                OSAdapterInfoData = (ADLAdapterInfoArray)Marshal.PtrToStructure(AdapterBuffer, OSAdapterInfoData.GetType());
                                                int IsActive = 0;

                                                for (int i = 0; i < NumberOfAdapters; i++) {
                                                    // Check if the adapter is active
                                                    if (null != ADL.ADL_Adapter_Active_Get)
                                                        ADLRet = ADL.ADL_Adapter_Active_Get(OSAdapterInfoData.ADLAdapterInfo[i].AdapterIndex, ref IsActive);

                                                    if (ADL.ADL_SUCCESS == ADLRet) {
                                                        // we are looking for amd
                                                        // TODO check discrete and integrated GPU separation
                                                        var vendorID = OSAdapterInfoData.ADLAdapterInfo[i].VendorID;
                                                        var devName = OSAdapterInfoData.ADLAdapterInfo[i].AdapterName;
                                                        if (vendorID == AMD_VENDOR_ID
                                                            || devName.ToLower().Contains("amd")
                                                            || devName.ToLower().Contains("radeon")
                                                            || devName.ToLower().Contains("firepro")) {
                                                            
                                                            var udid = OSAdapterInfoData.ADLAdapterInfo[i].UDID;
                                                            var pciVen_id_strSize = 21; // PCI_VEN_XXXX&DEV_XXXX
                                                            var uuid = udid.Substring(0, pciVen_id_strSize);
                                                            if (!_amdDeviceUUID.Contains(uuid)) {
                                                                _amdDeviceUUID.Add(uuid);
                                                                //_busIds.Add(OSAdapterInfoData.ADLAdapterInfo[i].BusNumber);
                                                                _amdDeviceName.Add(devName);
                                                            }
                                                        }
                                                    }
                                                }
                                            } else {
                                                Helpers.ConsolePrint(TAG, "ADL_Adapter_AdapterInfo_Get() returned error code " + ADLRet.ToString());
                                            }
                                        }
                                        // Release the memory for the AdapterInfo structure
                                        if (IntPtr.Zero != AdapterBuffer)
                                            Marshal.FreeCoTaskMem(AdapterBuffer);
                                    }
                                }
                                if (null != ADL.ADL_Main_Control_Destroy)
                                    ADL.ADL_Main_Control_Destroy();
                            } else {
                                // TODO
                                Helpers.ConsolePrint(TAG, "ADL_Main_Control_Create() returned error code " + ADLRet.ToString());
                                Helpers.ConsolePrint(TAG, "Check if ADL is properly installed!");
                            }
                        } catch (Exception ex) {
                            Helpers.ConsolePrint(TAG, "AMD ADL exception: " + ex.Message);
                            isAdlInit = false;
                        }
                        if(isAdlInit) {
                            if (amdGpus.Count == _amdDeviceUUID.Count) {
                                Helpers.ConsolePrint(TAG, "AMD OpenCL and ADL AMD query COUNTS GOOD/SAME");
                            } else {
                                Helpers.ConsolePrint(TAG, "AMD OpenCL and ADL AMD query COUNTS DIFFERENT/BAD");
                            }
                            for (int i_id = 0; i_id < amdGpus.Count; ++i_id) {
                                var deviceName = _amdDeviceName[i_id];
                                var newAmdDev = new AmdGpuDevice(amdGpus[i_id], deviceDriverOld[deviceName]);
                                newAmdDev.DeviceName = deviceName;
                                newAmdDev.UUID = _amdDeviceUUID[i_id];
                                string skipOrAdd = false ? "SKIPED" : "ADDED";
                                string etherumCapableStr = newAmdDev.IsEtherumCapable() ? "YES" : "NO";
                                string logMessage = String.Format("AMD OpenCL {0} device: {1}",
                                    skipOrAdd,
                                    String.Format("ID: {0}, NAME: {1}, UUID: {2}, MEMORY: {3}, ETHEREUM: {4}",
                                    newAmdDev.DeviceID.ToString(), newAmdDev.DeviceName, newAmdDev.UUID, 
                                    newAmdDev.DeviceGlobalMemory.ToString(), etherumCapableStr)
                                    );
                                new ComputeDevice(newAmdDev, true, true);
                                Helpers.ConsolePrint(TAG, logMessage);
                            }
                        }
                    }
                }
            }
        }

        private void UncheckedCPU() {
            // Auto uncheck CPU if any GPU is found
            var cdgm = ComputeDeviceGroupManager.Instance;
            if (cdgm.ContainsGPUs) cdgm.DisableCpuGroup();
        }

        #region NEW IMPLEMENTATION

        #region CUDA, NVIDIA Query

        string QueryCudaDevicesString = "";
        List<CudaDevice> CudaDevices = new List<CudaDevice>();
        private void QueryCudaDevicesOutputErrorDataReceived(object sender, DataReceivedEventArgs e) {
            if (e.Data != null) {
                QueryCudaDevicesString += e.Data;
            }
        }

        private bool IsSMGroupSkip(int sm_major) {
            if (sm_major == 6) {
                return ConfigManager.Instance.GeneralConfig.DeviceDetection.DisableDetectionNVidia6X;
            }
            if (sm_major == 5) {
                return ConfigManager.Instance.GeneralConfig.DeviceDetection.DisableDetectionNVidia5X;
            }
            if (sm_major == 3) {
                return ConfigManager.Instance.GeneralConfig.DeviceDetection.DisableDetectionNVidia3X;
            }
            if (sm_major == 2) {
                return ConfigManager.Instance.GeneralConfig.DeviceDetection.DisableDetectionNVidia2X;
            }
            return false;
        }

        private void QueryCudaDevices() {
            Process CudaDevicesDetection = new Process();
            CudaDevicesDetection.StartInfo.FileName = "CudaDeviceDetection.exe";
            CudaDevicesDetection.StartInfo.UseShellExecute = false;
            CudaDevicesDetection.StartInfo.RedirectStandardError = true;
            CudaDevicesDetection.StartInfo.RedirectStandardOutput = true;
            CudaDevicesDetection.StartInfo.CreateNoWindow = true;
            CudaDevicesDetection.OutputDataReceived += QueryCudaDevicesOutputErrorDataReceived;
            CudaDevicesDetection.ErrorDataReceived += QueryCudaDevicesOutputErrorDataReceived;

            const int waitTime = 5 * 1000; // 5seconds
            try {
                if (!CudaDevicesDetection.Start()) {
                    Helpers.ConsolePrint(TAG, "CudaDevicesDetection process could not start");
                } else {
                    CudaDevicesDetection.BeginErrorReadLine();
                    CudaDevicesDetection.BeginOutputReadLine();
                    if (CudaDevicesDetection.WaitForExit(waitTime)) {
                        CudaDevicesDetection.Close();
                    }
                }
            } catch (Exception ex) {
                // TODO
                Helpers.ConsolePrint(TAG, "CudaDevicesDetection threw Exception: " + ex.Message);
            } finally {
                if (QueryCudaDevicesString != "") {
                    try {
                        CudaDevices = JsonConvert.DeserializeObject<List<CudaDevice>>(QueryCudaDevicesString, Globals.JsonSettings);
                    } catch { }
                }
            }
            if (CudaDevices != null && CudaDevices.Count != 0) {
                StringBuilder stringBuilder = new StringBuilder();
                stringBuilder.AppendLine("");
                stringBuilder.AppendLine("CudaDevicesDetection:");
                foreach (var cudaDev in CudaDevices) {
                    // check sm vesrions
                    bool isUnderSM21;
                    {
                        bool isUnderSM2_major = cudaDev.SM_major < 2;
                        bool isUnderSM1_minor = cudaDev.SM_minor < 1;
                        isUnderSM21 = isUnderSM2_major && isUnderSM1_minor;
                    }
                    //bool isOverSM6 = cudaDev.SM_major > 6;
                    // TODO write that disabled group
                    bool isDisabledGroup = IsSMGroupSkip(cudaDev.SM_major);
                    bool skip = isUnderSM21 || isDisabledGroup;
                    string skipOrAdd = skip ? "SKIPED" : "ADDED";
                    string isDisabledGroupStr = isDisabledGroup ? " (SM group disabled)" : "";
                    string etherumCapableStr = cudaDev.IsEtherumCapable() ? "YES" : "NO";
                    stringBuilder.AppendLine(String.Format("\t{0} device{1}:", skipOrAdd, isDisabledGroupStr));
                    stringBuilder.AppendLine(String.Format("\t\tID: {0}", cudaDev.DeviceID.ToString()));
                    stringBuilder.AppendLine(String.Format("\t\tNAME: {0}", cudaDev.GetName()));
                    stringBuilder.AppendLine(String.Format("\t\tVENDOR: {0}", cudaDev.VendorName));
                    stringBuilder.AppendLine(String.Format("\t\tUUID: {0}", cudaDev.UUID));
                    stringBuilder.AppendLine(String.Format("\t\tSM: {0}", cudaDev.SMVersionString));
                    stringBuilder.AppendLine(String.Format("\t\tMEMORY: {0}", cudaDev.DeviceGlobalMemory.ToString()));
                    stringBuilder.AppendLine(String.Format("\t\tETHEREUM: {0}", etherumCapableStr));
                    
                    if (!skip) {
                        string group;
                        switch (cudaDev.SM_major) {
                            case 2:
                                group = GroupNames.GetName(DeviceGroupType.NVIDIA_2_1);
                                break;
                            case 3:
                                group = GroupNames.GetName(DeviceGroupType.NVIDIA_3_x);
                                break;
                            case 5:
                                group = GroupNames.GetName(DeviceGroupType.NVIDIA_5_x);
                                break;
                            case 6:
                                group = GroupNames.GetName(DeviceGroupType.NVIDIA_6_x);
                                break;
                            default:
                                group = GroupNames.GetName(DeviceGroupType.NVIDIA_6_x);
                                break;
                        }
                        new ComputeDevice(cudaDev, group, true);
                    }
                }
                Helpers.ConsolePrint(TAG, stringBuilder.ToString());
            } else {
                Helpers.ConsolePrint(TAG, "CudaDevicesDetection found no devices. CudaDevicesDetection returned: " + QueryCudaDevicesString);
            }
        }

        #endregion // CUDA, NVIDIA Query


        #region OpenCL Query

        private double GetNvidiaOpenCLDriver() {
            if (OpenCLJSONData != null) {
                List<OpenCLDevice> nvidiaOCLs = null;
                foreach (var oclPlatDevs in OpenCLJSONData.OCLPlatformDevices) {
                    if (oclPlatDevs.Key.ToLower().Contains("nvidia")) {
                        nvidiaOCLs = oclPlatDevs.Value;
                    }
                }

                if (nvidiaOCLs != null && nvidiaOCLs.Count > 0) {
                    if (Double.TryParse(nvidiaOCLs[0]._CL_DRIVER_VERSION,
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out _currentNvidiaOpenCLDriver)) {
                        return _currentNvidiaOpenCLDriver;
                    }
                }
            }

            return -1;
        }

        class OpenCLJSON {
            public Dictionary<string, int> OCLPlatforms = new Dictionary<string,int>();
            public Dictionary<string, List<OpenCLDevice>> OCLPlatformDevices = new Dictionary<string,List<OpenCLDevice>>();
        }
        string QueryOpenCLDevicesString = "";
        OpenCLJSON OpenCLJSONData = new OpenCLJSON();
        bool IsOpenCLQuerrySuccess = false;
        private void QueryOpenCLDevicesOutputErrorDataReceived(object sender, DataReceivedEventArgs e) {
            if (e.Data != null) {
                QueryOpenCLDevicesString += e.Data;
            }
        }

        private void QueryOpenCLDevices() {
            Process OpenCLDevicesDetection = new Process();
            OpenCLDevicesDetection.StartInfo.FileName = "OpenCLDeviceDetection.exe";
            OpenCLDevicesDetection.StartInfo.UseShellExecute = false;
            OpenCLDevicesDetection.StartInfo.RedirectStandardError = true;
            OpenCLDevicesDetection.StartInfo.RedirectStandardOutput = true;
            OpenCLDevicesDetection.StartInfo.CreateNoWindow = true;
            OpenCLDevicesDetection.OutputDataReceived += QueryOpenCLDevicesOutputErrorDataReceived;
            OpenCLDevicesDetection.ErrorDataReceived += QueryOpenCLDevicesOutputErrorDataReceived;

            const int waitTime = 5 * 1000; // 5seconds
            try {
                if (!OpenCLDevicesDetection.Start()) {
                    Helpers.ConsolePrint(TAG, "OpenCLDeviceDetection process could not start");
                } else {
                    OpenCLDevicesDetection.BeginErrorReadLine();
                    OpenCLDevicesDetection.BeginOutputReadLine();
                    if (OpenCLDevicesDetection.WaitForExit(waitTime)) {
                        OpenCLDevicesDetection.Close();
                    }
                }
            } catch(Exception ex) {
                // TODO
                Helpers.ConsolePrint(TAG, "OpenCLDeviceDetection threw Exception: " + ex.Message);
            } finally {
                if (QueryOpenCLDevicesString != "") {
                    try {
                        OpenCLJSONData = JsonConvert.DeserializeObject<OpenCLJSON>(QueryOpenCLDevicesString, Globals.JsonSettings);
                    } catch { }
                }
            }
            // TODO
            if (OpenCLJSONData == null) {
                Helpers.ConsolePrint(TAG, "OpenCLDeviceDetection found no devices. OpenCLDeviceDetection returned: " + QueryOpenCLDevicesString);
            } else {
                IsOpenCLQuerrySuccess = true;
                StringBuilder stringBuilder = new StringBuilder();
                stringBuilder.AppendLine("");
                stringBuilder.AppendLine("OpenCLDeviceDetection found devices success:");
                foreach (var kvp in OpenCLJSONData.OCLPlatformDevices) {
                    stringBuilder.AppendLine(String.Format("\tFound devices for platform: {0}", RemoveNullTerminator(kvp.Key)));
                    foreach (var oclDev in kvp.Value) {
                        stringBuilder.AppendLine("\t\tDevice:");
                        stringBuilder.AppendLine(String.Format("\t\t\tDevice ID {0}", oclDev.DeviceID));
                        stringBuilder.AppendLine(String.Format("\t\t\tDevice NAME {0}", RemoveNullTerminator(oclDev._CL_DEVICE_NAME)));
                        stringBuilder.AppendLine(String.Format("\t\t\tDevice TYPE {0}", RemoveNullTerminator(oclDev._CL_DEVICE_TYPE)));
                    }
                }
                Helpers.ConsolePrint(TAG, stringBuilder.ToString());
            }
        }

        private string RemoveNullTerminator(string inOut) {
            while (inOut.Contains("\0")) {
                inOut = inOut.Substring(0, inOut.Length - 1);
            }
            return inOut;
        } 

        #endregion OpenCL Query


        #endregion // NEW IMPLEMENTATION

    }
}

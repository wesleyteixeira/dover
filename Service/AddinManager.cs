﻿/*
 *  Dover Framework - OpenSource Development framework for SAP Business One
 *  Copyright (C) 2014  Eduardo Piva
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with this program.  If not, see <http://www.gnu.org/licenses/>.
 *  
 *  Contact me at <efpiva@gmail.com>
 * 
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using Dover.Framework.Attribute;
using Dover.Framework.DAO;
using Dover.Framework.Factory;
using Dover.Framework.Model;
using Dover.Framework.Model.SAP;
using Dover.Framework.Remoting;
using Castle.Core.Logging;
using Dover.Framework.Log;
using Dover.Framework.Interface;

namespace Dover.Framework.Service
{
    internal class ConfigAddin : MarshalByRefObject, IConfigAddin
    {

        public ILogger Logger { get; set; }
        public BusinessOneDAO b1DAO { get; set; }

        void IConfigAddin.ConfigureAddin(string addinName)
        {
            List<ResourceBOMAttribute> resourceAttr = new List<ResourceBOMAttribute>();
            Assembly assembly;

            try
            {
                assembly = Assembly.Load(addinName);
            }
            catch (InvalidOperationException e)
            {
                Logger.Error(String.Format(Messages.AddInNotFound, addinName), e);
                return;
            }
            ContainerManager.CheckProxy(assembly);

            var types = (from type in assembly.GetTypes()
                         where type.IsClass
                         select type);

            foreach (var type in types)
            {
                var attrs = type.GetCustomAttributes(true);
                foreach (var attr in attrs)
                {
                    Logger.Debug(DebugString.Format(Messages.ProcessingAttribute, attr, type));
                    if (attr is ResourceBOMAttribute)
                    {
                        resourceAttr.Add((ResourceBOMAttribute)attr);
                    }
                    else if (attr is PermissionAttribute)
                    {
                        b1DAO.UpdateOrSavePermissionIfNotExists((PermissionAttribute)attr);
                    }
                }
            }
            resourceAttr.Sort(); // need to order becase we need create tables, than fields and at the end UDOs.
            foreach (var resource in resourceAttr)
            {
                ProcessAddInResourceAttribute(resource, assembly);
            }
        }
        
        private void ProcessAddInResourceAttribute(ResourceBOMAttribute resourceBOMAttribute, Assembly asm)
        {
            using (var resourceStream = asm.GetManifestResourceStream(resourceBOMAttribute.ResourceName))
            {
                if (resourceStream == null)
                {
                    Logger.Error(string.Format(Messages.InternalResourceMissing, resourceBOMAttribute.ResourceName));
                    return;
                }
                switch (resourceBOMAttribute.Type)
                {
                    case ResourceType.UserField:
                        var userFieldBOM = b1DAO.GetBOMFromXML<UserFieldBOM>(resourceStream);
                        b1DAO.SaveBOMIfNotExists(userFieldBOM);
                        break;
                    case ResourceType.UserTable:
                        var userTableBOM = b1DAO.GetBOMFromXML<UserTableBOM>(resourceStream);
                        b1DAO.SaveBOMIfNotExists(userTableBOM);
                        break;
                    case ResourceType.UDO:
                        var udoBOM = b1DAO.GetBOMFromXML<UDOBOM>(resourceStream);
                        b1DAO.UpdateOrSaveBOMIfNotExists(udoBOM);
                        break;
                }
            }
        }

    }

    internal class AddInRunner
    {
        internal AssemblyInformation asm;
        internal ManualResetEvent shutdownEvent = new ManualResetEvent(false);
        internal ManualResetEvent bootEvent = new ManualResetEvent(false);
        /// <summary>
        /// This is the AddinManager of the Framework (creator of all Addins AppDomains)
        /// </summary>
        internal IAddinManager frameworkAddinManager;
        internal IFormEventHandler addinFormEventHandler;
        internal IEventDispatcher eventDispatcher;
        internal IAddinLoader addinLoader;

        internal Thread runnerThread;
        internal List<AddInRunner> runningAddins;
        internal Dictionary<string, AddInRunner> runningAddinsHash;

        internal AddInRunner(AssemblyInformation asm, IAddinManager frameworkAddinManager)
        {
            this.asm = asm;
            this.frameworkAddinManager = frameworkAddinManager;
        }

        internal void Run()
        {
            try
            {
                runningAddins.Add(this);
                runningAddinsHash.Add(asm.Code, this);

                var setup = new AppDomainSetup();
                setup.ApplicationName = "Dover.Inception";
                setup.ApplicationBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "addIn", asm.Namespace, asm.Name);
                var domain = AppDomain.CreateDomain("Dover.AddIn", null, setup);
                domain.SetData("shutdownEvent", shutdownEvent); // Thread synchronization
                domain.SetData("bootEvent", bootEvent);
                domain.SetData("assemblyName", asm.Name); // Used to get current AssemblyName for logging and reflection
                domain.SetData("frameworkManager", frameworkAddinManager);
                IApplication app = (IApplication)domain.CreateInstanceAndUnwrap("Framework", "Dover.Framework.Application");
                SAPServiceFactory.PrepareForInception(domain);
                addinFormEventHandler = app.Resolve<IFormEventHandler>();
                addinLoader = app.Resolve<IAddinLoader>();
                eventDispatcher = app.Resolve<IEventDispatcher>();
                Sponsor<IApplication> appSponsor = new Sponsor<IApplication>(app);
                Sponsor<IFormEventHandler> formEventSponsor = new Sponsor<IFormEventHandler>(addinFormEventHandler);
                Sponsor<IAddinLoader> loaderSponsor = new Sponsor<IAddinLoader>(addinLoader);
                Sponsor<IEventDispatcher> eventSponsor = new Sponsor<IEventDispatcher>(eventDispatcher);
                
                app.RunAddin();
                AppDomain.Unload(domain);
            }
            catch (AppDomainUnloadedException e)
            {
                // ignore and continue shutdown.
            }
            finally
            {
                runningAddins.Remove(this);
                runningAddinsHash.Remove(asm.Name);
            }
        } 
    }

    internal class AddinManager : MarshalByRefObject, IAddinManager
    {
        public ILogger Logger { get; set; }
        private bool _initialized;
        private PermissionManager permissionManager;
        private IAddinLoader addinLoader;
        private AssemblyDAO assemblyDAO;
        private FileUpdate fileUpdate;
        private BusinessOneDAO b1DAO;
        private LicenseManager licenseManager;

        private List<AddInRunner> runningAddIns = new List<AddInRunner>();
        private Dictionary<string, AddInRunner> runningAddinsHash = new Dictionary<string, AddInRunner>();
         
        private I18NService i18nService;

        public AddinManager(PermissionManager permissionManager, FileUpdate fileUpdate,
            BusinessOneDAO b1DAO, I18NService i18nService, AssemblyDAO assemblyDAO, IAddinLoader addinLoader,
            LicenseManager licenseManager)
        {
            _initialized = false;
            this.permissionManager = permissionManager;
            this.assemblyDAO = assemblyDAO;
            this.b1DAO = b1DAO;
            this.i18nService = i18nService;
            this.fileUpdate = fileUpdate;
            this.addinLoader = addinLoader;
            this.licenseManager = licenseManager;
        }

        void IAddinManager.LoadAddins(List<AssemblyInformation> addins)
        {
            this.LoadAddins(addins);
        }

        protected internal virtual void LoadAddins(List<AssemblyInformation> addins)
        {
            var authorizedAddins = FilterAuthorizedAddins(addins);
            foreach (var addin in authorizedAddins)
            {
                try
                {
                    LoadAddin(addin);
                }
                catch (Exception e)
                {
                    Logger.Error(string.Format(Messages.StartThisError, addin), e);
                }
            }
        }

        internal List<AssemblyInformation> ListAddins()
        {
            return assemblyDAO.GetAssembliesInformation(AssemblyType.Addin);
        }

        private void LoadAddin(AssemblyInformation addin)
        {
            DateTime dueDate;
            if (licenseManager.AddinIsValid(addin.Code, out dueDate))
            {
                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "addIn", addin.Namespace, addin.Name);
                Directory.CreateDirectory(directory);
                fileUpdate.UpdateAppDataFolder(addin, directory);
                if (!IsInstalled(addin.Code))
                {
                    InstallAddin(addin, directory);
                }
                RegisterAddin(addin);
                ConfigureLog(addin);
            }
            else
            {
                Logger.Error(string.Format(Messages.NoLicenseError, addin.Name));
            }
        }

        private void InstallAddin(AssemblyInformation addin, string baseDirectory)
        {
            try
            {
                Logger.Info(string.Format(Messages.ConfiguringAddin, addin.Name));
                ConfigureAddin(addin, baseDirectory);
                MarkAsInstalled(addin.Code);
                Logger.Info(string.Format(Messages.ConfiguredAddin, addin.Name));
            }
            catch (Exception e)
            {
                MarkAsNotInstalled(addin.Code);
                throw e;
            }
        }

        private void MarkAsNotInstalled(string addInCode)
        {
            b1DAO.ExecuteStatement(
    string.Format(this.GetSQL("MarkAsNotInstalled.sql"), addInCode));
        }

        private void MarkAsInstalled(string addInCode)
        {
            b1DAO.ExecuteStatement(
    string.Format(this.GetSQL("MarkAsInstalled.sql"), addInCode));
        }

        private void ConfigureLog(AssemblyInformation addin)
        {
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dover.Framework.DoverAddin.config"))
            {
                var destination = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "addIn", addin.Namespace, addin.Name, addin.Name + ".config");

                if (source != null && !File.Exists(destination))
                {
                    var doc = XDocument.Load(source);

                    var query = from c in doc.Elements("configuration").Elements("log4net")
                                .Elements("appender").Elements("file")
                                select c;

                    foreach (var fileTag in query)
                    {
                        fileTag.Attribute("value").Value = addin.Name + ".log";
                    }

                    doc.Save(destination);
                }
            }
        }

        bool IAddinManager.CheckAddinConfiguration(string assemblyName, out string xmlDataTable,
                                    out string addinName, out string addinNamespace)
        {
            Assembly assembly = Assembly.Load(assemblyName);
            XDocument dataTable = CreateDTXML();

            bool isValid = false;
            addinName = string.Empty;
            addinNamespace = string.Empty;
            string tempComments = string.Empty;
            xmlDataTable = string.Empty;

            var types = (from type in assembly.GetTypes()
                         where type.IsClass
                         select type);

            foreach (var type in types)
            {
                var attrs = type.GetCustomAttributes(true);
                foreach (var attr in attrs)
                {
                    Logger.Debug(DebugString.Format(Messages.ProcessingAttribute, attr, type));
                    if (attr is ResourceBOMAttribute)
                    {
                        CheckAddInAttribute((ResourceBOMAttribute)attr, assembly, dataTable);
                    }
                    else if (attr is PermissionAttribute)
                    {
                        CheckPermissionAttribute((PermissionAttribute)attr, dataTable);
                    }
                    else if (attr is AddInAttribute &&
                        (!string.IsNullOrWhiteSpace(((AddInAttribute)attr).Description)
                         || !string.IsNullOrWhiteSpace(((AddInAttribute)attr).i18n))
                        && !string.IsNullOrWhiteSpace(((AddInAttribute)attr).Name)
                        && !string.IsNullOrWhiteSpace(((AddInAttribute)attr).Namespace))
                    {
                        isValid = true;
                        addinName = ((AddInAttribute)attr).Name;
                        addinNamespace = ((AddInAttribute)attr).Namespace;
                    }
                }
            }

            var rows = dataTable.Element("DataTable").Element("Rows").Elements("Row");
            
            if (rows != null && rows.Count() > 0)
                xmlDataTable = dataTable.ToString();

            return isValid;
        }

        private XDocument CreateDTXML()
        {
            XDocument dt = new XDocument();
            var root = new XElement("DataTable");
            root.SetAttributeValue("Uid", "dbchange");
            dt.Add(root);
            root.Add(new XElement("Rows"));

            return dt;
        }

        private void CheckPermissionAttribute(PermissionAttribute permissionAttribute, XDocument dataTable)
        {
            if (!b1DAO.PermissionExists(permissionAttribute))
            {
                var rows = dataTable.Element("DataTable").Element("Rows");
                rows.Add(Messages.Permission, permissionAttribute.PermissionID, permissionAttribute.Name);
            }
        }

        private void CheckAddInAttribute(ResourceBOMAttribute resourceBOMAttribute, Assembly asm, XDocument dataTable)
        {
            using (var resourceStream = asm.GetManifestResourceStream(resourceBOMAttribute.ResourceName))
            {
                if (resourceStream == null)
                {
                    Logger.Error(string.Format(Messages.InternalResourceMissing, resourceBOMAttribute.ResourceName));
                }
                switch (resourceBOMAttribute.Type)
                {
                    case ResourceType.UserField:
                        var userFieldBOM = b1DAO.GetBOMFromXML<UserFieldBOM>(resourceStream);
                        UpdateDataTableMissingItems(dataTable, userFieldBOM, Messages.UserField);
                        break;
                    case ResourceType.UserTable:
                        var userTableBOM = b1DAO.GetBOMFromXML<UserTableBOM>(resourceStream);
                        UpdateDataTableMissingItems(dataTable, userTableBOM, Messages.UserTable);
                        break;
                    case ResourceType.UDO:
                        var udoBOM = b1DAO.GetBOMFromXML<UDOBOM>(resourceStream);
                        UpdateDataTableOutdatedItems(dataTable, udoBOM, Messages.UDO);
                        break;
                }
            }
        }

        private void UpdateDataTableOutdatedItems(XDocument dataTable, UDOBOM bom, string bomName)
        {
            List<int> keys = b1DAO.ListOutdatedBOMKeys(bom);
            UpdateDataTableFromKeys(bom, keys, dataTable, bomName);
        }

        private void UpdateDataTableMissingItems(XDocument dataTable, IBOM bom, string bomName)
        {
            List<int> keys = b1DAO.ListMissingBOMKeys(bom);
            UpdateDataTableFromKeys(bom, keys, dataTable, bomName);
        }

        private void UpdateDataTableFromKeys(IBOM bom, List<int> keys, XDocument dataTable, string bomName)
        {
            var rows = dataTable.Element("DataTable").Element("Rows");
            if (rows == null)
            {
                rows = new XElement("Rows");
                dataTable.Element("DataTable").Add(rows);
            }

            foreach (var key in keys)
            {
                rows.Add(CreateRow(bomName, bom.BO[key].GetFormattedKey(),
                    bom.BO[key].GetFormattedDescription()));
            }
        }

        private XElement CreateRow(string type, string value, string description)
        {
            XElement row = new XElement("Row");
            XElement cells = new XElement("Cells");
            XElement typeCell = new XElement("Cell");
            XElement typeUID = new XElement("ColumnUid");
            XElement typeValue = new XElement("Value");

            row.Add(cells);
            cells.Add(typeCell);
            typeCell.Add(typeUID);
            typeCell.Add(typeValue);
            typeUID.Value = "type";
            typeValue.Value = type;

            typeCell = new XElement("Cell");
            typeUID = new XElement("ColumnUid");
            typeValue = new XElement("Value");
            typeCell.Add(typeUID);
            typeCell.Add(typeValue);
            cells.Add(typeCell);
            typeUID.Value = "key";
            typeValue.Value = value;

            typeCell = new XElement("Cell");
            typeUID = new XElement("ColumnUid");
            typeValue = new XElement("Value");
            typeCell.Add(typeUID);
            typeCell.Add(typeValue);
            cells.Add(typeCell);
            typeUID.Value = "description";
            typeValue.Value = description;

            return row;
        }

        private void ConfigureAddin(AssemblyInformation addin, string baseDirectory)
        {
            Logger.Info(String.Format(Messages.ConfiguringAddin, addin));
            var setup = new AppDomainSetup();
            setup.ApplicationName = "Dover.ConfigureDomain";
            setup.ApplicationBase = baseDirectory;
            AppDomain configureDomain = AppDomain.CreateDomain("ConfigureDomain", null, setup);
            try
            {
                IApplication app = (IApplication)configureDomain.CreateInstanceAndUnwrap("Framework",
                    "Dover.Framework.Application");
                SAPServiceFactory.PrepareForInception(configureDomain);
                IConfigAddin addinConfig = app.Resolve<IConfigAddin>();
                addinConfig.ConfigureAddin(addin.Name);
            }
            finally
            {
                AppDomain.Unload(configureDomain);
            }
        }

        private void RegisterAddin(AssemblyInformation addin)
        {
            AddInRunner runner = new AddInRunner(addin, this);
            var thread = new Thread(new ThreadStart(runner.Run));
            thread.SetApartmentState(ApartmentState.STA);
            i18nService.ConfigureThreadI18n(thread);
            runner.runnerThread = thread;
            runner.runningAddins = runningAddIns;
            runner.runningAddinsHash = runningAddinsHash;
            thread.Start();
            runner.bootEvent.WaitOne();
        }

        private List<AssemblyInformation> FilterAuthorizedAddins(List<AssemblyInformation> addins)
        {
            var authorized = new List<AssemblyInformation>();
            foreach (var addin in addins)
            {
                if (permissionManager.AddInEnabled(addin.Code))
                    authorized.Add(addin);
            }
            return authorized;
        }

        void IAddinManager.ConfigureAddinsI18N()
        {
            i18nService.ConfigureThreadI18n(Thread.CurrentThread);
            foreach (var addin in runningAddIns)
            {
                i18nService.ConfigureThreadI18n(addin.runnerThread);
                addin.addinLoader.StartMenu();
                addin.addinFormEventHandler.RegisterForms(false);
            }
            ((IAddinLoader)addinLoader).StartMenu();
            
        }

         private bool IsInstalled(string code)
        {
            string installedFlag = b1DAO.ExecuteSqlForObject<string>(
                string.Format(this.GetSQL("IsInstalled.sql"), code));
            return !string.IsNullOrEmpty(installedFlag) && installedFlag == "Y";
        }

         void IAddinManager.ShutdownAddins()
         {
             this.ShutdownAddins();
         }

        [Transaction]
        protected virtual void ShutdownAddins()
        {
            // prevent list modification sync issues on shutdown.
            var runningAddinsTemp = new List<AddInRunner>(runningAddIns);
            foreach (var runner in runningAddinsTemp)
            {
                Logger.Info(string.Format(Messages.ShutdownAddin, runner.asm.Name));
                runner.eventDispatcher.UnregisterEvents();
                runner.addinFormEventHandler.UnRegisterForms();
                runner.shutdownEvent.Set();
                runner.runnerThread.Join();
            }
            runningAddIns = new List<AddInRunner>(); // clean running AddIns.
            runningAddinsHash = new Dictionary<string, AddInRunner>(); // clean-up.
            System.Windows.Forms.Application.Exit(); // free main Inception thread.
        }

        AddinStatus IAddinManager.GetAddinStatus(string addinCode)
        {
            return (runningAddinsHash.ContainsKey(addinCode)) ? AddinStatus.Running : AddinStatus.Stopped;
        }

        void IAddinManager.ShutdownAddin(string addinCode)
        {
            this.ShutdownAddin(addinCode);
        }

        [Transaction]
        protected internal virtual void ShutdownAddin(string addinCode)
        {
            AddInRunner addin;
            runningAddinsHash.TryGetValue(addinCode, out addin);
            if (addin != null)
            {
                runningAddIns.Remove(addin);
                runningAddinsHash.Remove(addinCode);
                Logger.Info(string.Format(Messages.ShutdownAddin, addin.asm.Name));
                addin.eventDispatcher.UnregisterEvents();
                addin.addinFormEventHandler.UnRegisterForms();
                addin.shutdownEvent.Set();
            }
        }

        void IAddinManager.StartAddin(string addinCode)
        {
            this.StartAddin(addinCode);
        }

        [Transaction]
        protected internal virtual void StartAddin(string addinCode)
        {
            AssemblyInformation asmInfo = assemblyDAO.GetAssemblyInformation(addinCode);
            if (asmInfo != null)
                LoadAddin(asmInfo);
        }

        void IAddinManager.InstallAddin(string addinCode)
        {
            this.InstallAddin(addinCode);
        }

        [Transaction]
        protected internal virtual void InstallAddin(string addinCode)
        {
            AssemblyInformation asmInfo = assemblyDAO.GetAssemblyInformation(addinCode);
            if (asmInfo != null)
            {
                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "addIn", asmInfo.Namespace, asmInfo.Name);
                Directory.CreateDirectory(directory);
                fileUpdate.UpdateAppDataFolder(asmInfo, directory);
                InstallAddin(asmInfo, directory);
            }
        }
        
        string IAddinManager.GetAddinChangeLog(string addinCode)
        {
            throw new NotImplementedException("replace with database stored information");
        }

        void IAddinManager.LogError(string msg)
        {
            Logger.Error(msg);
        }

        bool IAddinManager.Initialized
        {
            get
            {
                return _initialized;
            }
            set
            {
                _initialized = value;
            }
        }
    }
}

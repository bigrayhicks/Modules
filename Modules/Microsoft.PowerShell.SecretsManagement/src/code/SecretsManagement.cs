// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Management.Automation;

using Dbg = System.Diagnostics.Debug;

namespace Microsoft.PowerShell.SecretsManagement
{
    #region Register-SecretsVault

    /// <summary>
    /// Cmdlet to register a remote secrets vaults provider module
    /// </summary>
    [Cmdlet(VerbsLifecycle.Register, "SecretsVault", SupportsShouldProcess = true)]
    public sealed class RegisterSecretsVaultCommand : PSCmdlet
    {
        #region Parameters

        /// <summary>
        /// Gets or sets a friendly name for the registered secrets vault.
        /// The name must be unique.
        /// </summary>
        [Parameter(Position=0, Mandatory=true)]
        [ValidateNotNullOrEmpty]
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the path of the vault extension module to register.
        /// </summary>
        [Parameter(Position=1, Mandatory=true)]
        [ValidateNotNullOrEmpty]
        public string ModulePath { get; set; }

        /// <summary>
        /// Gets or sets an optional ScriptBlock used to retrieve a secret from the registered vault.
        /// Note: We can only store the ScriptBlock text, and this means we will need to have an internal
        ///       API that creates a ScriptBlock from an untrusted source, which in turn means that the
        ///       ScriptBlock is created in CL mode on locked down systems.
        /// </summary>
        [Parameter]
        [ValidateNotNull]
        public ScriptBlock SecretGetScript { get; set; }

        /// <summary>
        /// Gets or sets an optional Hashtable of parameters to the registered vault extension module 
        /// script used to retrieve secrets from the vault.
        /// Note: This may contain sensitive information, and so should be stored in local vault.
        /// </summary>
        [Parameter]
        public Hashtable SecretGetParameters { get; set; }

        /// <summary>
        /// Gets or sets an optional ScriptBlock used to add a secret to the registered vault.
        /// Note: Add script block text only and convert to ScriptBlock (safely) as needed.
        /// </summary>
        [Parameter]
        [ValidateNotNull]
        public ScriptBlock SecretSetScript { get; set; }

        /// <summary>
        /// Gets or sets an optional Hashtable of parameters to the registered vault extension module
        /// script used to add secrets to the vault.
        /// </summary>
        [Parameter]
        public Hashtable SecretSetParameters { get; set; }

        /// <summary>
        /// Gets or sets an optional ScriptBlock used to remove a secret from the registered vault.
        /// </summary>
        [Parameter]
        [ValidateNotNull]
        public ScriptBlock SecretRemoveScript { get; set; }

        /// <summary>
        /// Gets or sets an optional Hashtable of parameters to the registered vault extension module
        /// script used to remove secrets from the vault.
        /// </summary>
        [Parameter]
        public Hashtable SecretRemoveParameters { get; set; }

        #endregion

        #region Overrides

        protected override void EndProcessing()
        {
            var vaultInfo = new Hashtable();

            // Validate mandatory parameters.
            var vaultItems = RegisteredVaultCache.GetAll();
            if (vaultItems.ContainsKey(Name))
            {
                ThrowTerminatingError(
                    new ErrorRecord(
                        new InvalidOperationException("Provided Name for vault is already being used."),
                        "RegisterSecretsVaultInvalidVaultName",
                        ErrorCategory.InvalidArgument,
                        this));
            }

            var filePaths = this.GetResolvedProviderPathFromPSPath(ModulePath, out ProviderInfo _);
            if (filePaths.Count == 0)
            {
                ThrowTerminatingError(
                    new ErrorRecord(
                        new PSArgumentException("Provided ModulePath is not valid."),
                        "RegisterSecretsVaultInvalidModulePath",
                        ErrorCategory.InvalidArgument,
                        this));
            }
            ModulePath = filePaths[0];
            vaultInfo.Add(ExtensionVaultModule.ModulePathStr, ModulePath);

            if (!ShouldProcess(Name, VerbsLifecycle.Register))
            {
                return;
            }

            // Get module information (don't load it because binary modules cannot be re-loaded).
            var results = InvokeCommand.InvokeScript(
                script: @"
                    param ([string] $ModulePath)

                    Get-Module -Name $ModulePath -List
                ",
                args: new object[] { ModulePath });
            PSModuleInfo moduleInfo = results[0].BaseObject as PSModuleInfo;
            if (moduleInfo == null)
            {
                ThrowTerminatingError(
                    new ErrorRecord(
                        new PSInvalidOperationException("Could not retrieve module information."),
                        "RegisterSecretsVaultCantGetModuleInfo",
                        ErrorCategory.InvalidOperation,
                        this));
            }
            vaultInfo.Add(ExtensionVaultModule.ModuleNameStr, moduleInfo.Name);

            // Check for supported cmdlets
            bool hasGet = moduleInfo.ExportedCommands.ContainsKey(ExtensionVaultModule.DefaultSecretGetCmd);
            vaultInfo.Add(ExtensionVaultModule.HaveDefaultGetStr, hasGet);
            bool hasSet = moduleInfo.ExportedCommands.ContainsKey(ExtensionVaultModule.DefaultSecretSetCmd);
            vaultInfo.Add(ExtensionVaultModule.HaveDefaultSetStr, hasSet);
            bool hasRemove = moduleInfo.ExportedCommands.ContainsKey(ExtensionVaultModule.DefaultSecretRemoveCmd);
            vaultInfo.Add(ExtensionVaultModule.HaveDefaultRemoveStr, hasRemove);

            // Sanity check for required Get-Secret.
            if (!hasGet && SecretGetScript == null)
            {
                ThrowTerminatingError(
                    new ErrorRecord(
                        new PSArgumentException("The required Get-Secret cmdlet or script block was not found."),
                        "RegisterSecretsVaultMissingGetSecret",
                        ErrorCategory.InvalidArgument,
                        this));
            }

            vaultInfo.Add(
                key: ExtensionVaultModule.SecretGetScriptStr, 
                value: SecretGetScript?.ToString());

            StoreSecretParameters(
                vaultInfo: vaultInfo,
                key: ExtensionVaultModule.SecretGetParamsStr,
                parameters: SecretGetParameters);

            vaultInfo.Add(
                key: ExtensionVaultModule.SecretSetScriptStr, 
                value: SecretSetScript?.ToString());

            StoreSecretParameters(
                vaultInfo: vaultInfo,
                key: ExtensionVaultModule.SecretSetParamsStr,
                parameters: SecretSetParameters);

            vaultInfo.Add(
                key: ExtensionVaultModule.SecretRemoveScriptStr,
                value: SecretRemoveScript?.ToString());

            StoreSecretParameters(
                vaultInfo: vaultInfo,
                key: ExtensionVaultModule.SecretRemoveParamsStr,
                parameters: SecretRemoveParameters);

            // Register new secret vault information.
            RegisteredVaultCache.Add(
                keyName: Name,
                vaultInfo: vaultInfo);
        }

        #endregion

        #region Private methods

        private void StoreSecretParameters(
            Hashtable vaultInfo,
            string key,
            Hashtable parameters)
        {
            var parametersName = string.Empty;

            if (parameters != null)
            {
                // Generate unique name for parameters based on vault name.
                parametersName = Name + "_" + key + "_" + ((new System.Random()).Next(100)).ToString();

                // Store parameters in built-in local secure vault.
                int errorCode = 0;
                if (!LocalSecretStore.WriteObject(
                    name: parametersName,
                    parameters,
                    ref errorCode))
                {
                    var msg = string.Format(
                        CultureInfo.InvariantCulture, 
                        "Unable to register vault extension because writing script parameters to the built-in local store failed with error: {0}",
                        LocalSecretStore.GetErrorMessage(errorCode));

                    ThrowTerminatingError(
                        new ErrorRecord(
                            new PSInvalidOperationException(msg),
                            "RegisterSecretsVaultCannotSaveParameters",
                            ErrorCategory.WriteError,
                            this));
                }
            }
            
            // Add parameters store name to the vault registry information.
            vaultInfo.Add(
                key: key,
                value: parametersName);
        }

        #endregion
    }

    #endregion

    #region Unregister-SecretsVault

    /// <summary>
    /// Cmdlet to unregister a secrets vault.
    /// </summary>
    [Cmdlet(VerbsLifecycle.Unregister, "SecretsVault", SupportsShouldProcess = true)]
    public sealed class UnregisterSecretsVaultCommand : PSCmdlet
    {
        #region Parameters

        /// <summary>
        /// Gets or sets a name of the secrets vault to unregister.
        /// </summary>
        [Parameter(Position=0, Mandatory=true)]
        [ValidateNotNullOrEmpty]
        public string Name { get; set; }

        #endregion

        #region Overrides

        protected override void EndProcessing()
        {
            if (!ShouldProcess(Name, VerbsLifecycle.Unregister))
            {
                return;
            }

            var removedVaultInfo = RegisteredVaultCache.Remove(Name);

            // Remove any parameter secrets from built-in local store.
            RemoveParamSecrets(removedVaultInfo, ExtensionVaultModule.SecretGetParamsStr);
            RemoveParamSecrets(removedVaultInfo, ExtensionVaultModule.SecretSetParamsStr);
            RemoveParamSecrets(removedVaultInfo, ExtensionVaultModule.SecretRemoveParamsStr);
        }

        #endregion

        #region Private methods

        private static void RemoveParamSecrets(
            Hashtable vaultInfo,
            string ParametersNameKey)
        {
            if (vaultInfo != null && vaultInfo.ContainsKey(ParametersNameKey))
            {
                var parametersName = (string) vaultInfo[ParametersNameKey];
                if (!string.IsNullOrEmpty(parametersName))
                {
                    int errorCode = 0;
                    if (!LocalSecretStore.DeleteObject(parametersName, ref errorCode))
                    {
                        Dbg.Assert(false, "Parameter secrets should always be removed from local store.");
                    }
                }
            }
        }

        #endregion
    }

    #endregion

    #region Get-SecretsVault

    #region SecretVaultInfo

    /// <summary>
    /// Class that contains secret vault information.
    /// </summary>
    public sealed class SecretVaultInfo
    {
        public string Name { get; }
        public string ModuleName { get; }
        public string ModulePath { get; }
        public bool HaveDefaultGet { get; }
        public bool HaveDefaultSet { get; }
        public bool HaveDefaultRemove { get; }
        public string SecretGetScript { get; }
        public string SecretGetParamsName { get; }
        public string SecretSetScript { get; }
        public string SecretSetParamsName { get; }
        public string SecretRemoveScript { get; }
        public string SecretRemoveParamsName { get; }

        public SecretVaultInfo(
            string name,
            Hashtable vaultInfo)
        {
            Name = name;
            ModuleName = (vaultInfo.ContainsKey(nameof(ModuleName)))
                ? (string) vaultInfo[nameof(ModuleName)] : string.Empty;
            ModulePath = (vaultInfo.ContainsKey(nameof(ModulePath)))
                ? (string) vaultInfo[nameof(ModulePath)] : string.Empty;
            HaveDefaultGet = (vaultInfo.ContainsKey(nameof(HaveDefaultGet)))
                ? (bool) vaultInfo[nameof(HaveDefaultGet)] : false;
            HaveDefaultSet = (vaultInfo.ContainsKey(nameof(HaveDefaultSet)))
                ? (bool) vaultInfo[nameof(HaveDefaultSet)] : false;
            HaveDefaultRemove = (vaultInfo.ContainsKey(nameof(HaveDefaultRemove)))
                ? (bool) vaultInfo[nameof(HaveDefaultRemove)] : false;
            SecretGetScript = (vaultInfo.ContainsKey(nameof(SecretGetScript)))
                ? (string) vaultInfo[nameof(SecretGetScript)] : string.Empty;
            SecretGetParamsName = (vaultInfo.ContainsKey(nameof(SecretGetParamsName)))
                ? (string) vaultInfo[nameof(SecretGetParamsName)] : string.Empty;
            SecretSetScript = (vaultInfo.ContainsKey(nameof(SecretSetScript)))
                ? (string) vaultInfo[nameof(SecretSetScript)] : string.Empty;
            SecretSetParamsName = (vaultInfo.ContainsKey(nameof(SecretSetParamsName)))
                ? (string) vaultInfo[nameof(SecretSetParamsName)] : string.Empty;
            SecretRemoveScript = (vaultInfo.ContainsKey(nameof(SecretRemoveScript)))
                ? (string) vaultInfo[nameof(SecretRemoveScript)] : string.Empty;
            SecretRemoveParamsName = (vaultInfo.ContainsKey(nameof(SecretRemoveParamsName)))
                ? (string) vaultInfo[nameof(SecretRemoveParamsName)] : string.Empty;
        }
    }

    #endregion

    /// <summary>
    /// Cmdlet to return registered secret vaults as SecretVaultInfo objects.
    /// If no name is provided then all registered secret vaults will be returned.
    /// </summary>
    [Cmdlet(VerbsCommon.Get, "SecretsVault")]
    public sealed class GetSecretsVaultCommand : PSCmdlet
    {
        #region Parameters

        /// <summary>
        /// Gets or sets an optional name of the secret vault to return.
        /// <summary>
        [Parameter(Position=0)]
        public string Name { get; set; }

        #endregion

        #region Overrides

        protected override void EndProcessing()
        {
            var namePattern = new WildcardPattern(
                (!string.IsNullOrEmpty(Name)) ? Name : "*", 
                WildcardOptions.IgnoreCase);

            Hashtable items = RegisteredVaultCache.GetAll();
            foreach(DictionaryEntry item in items)
            {
                if (namePattern.IsMatch((string) item.Key))
                {
                    WriteObject(
                        new SecretVaultInfo(
                            name: (string) item.Key, 
                            vaultInfo: (Hashtable) item.Value));
                }
            }
        }

        #endregion
    }

    #endregion

    #region Add-Secret

    /// <summary>
    /// Adds a provided secret to the local default vault.
    /// </summary>
    [Cmdlet(VerbsCommon.Add, "Secret")]
    public sealed class AddSecretCommand : PSCmdlet
    {
        #region Parameters

        /// <summary>
        /// Gets or sets a name of the secret to be added.
        /// </summary>
        [Parameter(Mandatory=true)]
        [ValidateNotNullOrEmpty]
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets a value that is the secret to be added.
        /// Supported types:
        ///     PSCredential
        ///     SecureString
        ///     String
        ///     Hashtable
        ///     byte[]
        /// </summary>
        [Parameter(Mandatory=true)]
        public object Secret { get; set; }

        /// <summary>
        /// Gets or sets a flag indicating whether an existing secret with the same name is overwritten.
        /// </summary>
        [Parameter]
        public SwitchParameter NoClobber { get; set; }

        #endregion

        #region Overrides

        protected override void ProcessRecord()
        {
            // TODO: 
        }

        #endregion

    }

    #endregion

    #region Remove-Secret

    /// <summary>
    /// Removes a secret by name from the local default vault.
    /// <summary>
    [Cmdlet(VerbsCommon.Remove, "Secret")]
    public sealed class RemoveSecretCommand : PSCmdlet
    {
        #region Parameters

        /// <summary>
        /// Gets or sets a name of the secret to be removed.
        /// </summary>
        [Parameter(Mandatory=true)]
        [ValidateNotNullOrEmpty]
        public string Name { get; set; }

        #endregion

        #region Overrides

        protected override void ProcessRecord()
        {
            // TODO: 
        }

        #endregion

    }

    #endregion

    #region Get-Secret

    /// <summary>
    /// Retrieves a secret by name.
    /// If no name is provided then returns all secrets from vault.
    /// </summary>
    [Cmdlet(VerbsCommon.Get, "Secret")]
    [OutputType(typeof(object))]
    public sealed class GetSecretCommand : PSCmdlet
    {
        #region Parameters

        /// <summary>
        /// Gets or sets an optional name of secret to retrieve.
        [Parameter(Position=0)]
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets an optional name of the vault to retrieve the secret from.
        /// If no vault name is specified then the secret will be retrieved from the default (local) vault.
        /// </summary>
        [Parameter]
        public string Vault { get; set; }

        #endregion

        #region Overrides

        protected override void EndProcessing()
        {
            // Search single vault.
            if (!string.IsNullOrEmpty(Vault))
            {
                if (!RegisteredVaultCache.VaultExtensions.TryGetValue(
                    key: Vault,
                    value: out ExtensionVaultModule extensionModule))
                {
                    var msg = string.Format(CultureInfo.InvariantCulture, "Vault not found in registry: {0}", Vault);

                    ThrowTerminatingError(
                        new ErrorRecord(
                            new PSInvalidOperationException(msg),
                            "GetSecretVaultNotFound",
                            ErrorCategory.ObjectNotFound,
                            this));
                }

                extensionModule.InvokeGetSecret(this, Name);
                return;
            }

            // Search through all vaults.
            foreach (var extensionModule in RegisteredVaultCache.VaultExtensions.Values)
            {
                extensionModule.InvokeGetSecret(this, Name);
            }
        }

        #endregion
    }

    #endregion

    #region Test Only

    [Cmdlet(VerbsCommon.Add, "LocalSecret")]
    public sealed class AddLocalSecretCommand : PSCmdlet
    {
        [Parameter(Position=0, Mandatory=true)]
        [ValidateNotNullOrEmpty]
        public string Name { get; set; }

        [Parameter(Position=1, Mandatory=true)]
        [ValidateNotNullOrEmpty]
        public PSObject Secret { get; set; }

        protected override void EndProcessing()
        {
            try
            {
                int errorCode = 0;
                if (!LocalSecretStore.WriteObject(
                    Name,
                    Secret.BaseObject,
                    ref errorCode))
                {
                    var msg = LocalSecretStore.GetErrorMessage(errorCode);
                    WriteError(
                        new ErrorRecord(
                            new System.InvalidOperationException(
                                string.Format(CultureInfo.InvariantCulture, "Failed to write secret {0} to vault, with error: {1}", Name, msg)),
                                "SecretWriteError",
                                ErrorCategory.InvalidOperation,
                                this));
                }
            }
            catch (System.Exception ex)
            {
                WriteError(
                    new ErrorRecord(
                        ex,
                        "SecretWriteError",
                        ErrorCategory.InvalidOperation,
                        this));
            }
        }
    }

    [Cmdlet(VerbsCommon.Get, "LocalSecret")]
    public sealed class GetLocalSecretCommand : PSCmdlet
    {
        [Parameter(Position=0)]
        public string Name { get; set; }

        [Parameter]
        public SwitchParameter All { get; set; }

        protected override void EndProcessing()
        {
            int errorCode = 0;
            string msg = null;

            if (!string.IsNullOrEmpty(Name) && (Name.IndexOf('*') < 0))
            {
                if (LocalSecretStore.ReadObject(
                    Name,
                    out object outObject,
                    ref errorCode))
                {
                    WriteObject(outObject);
                    return;
                }

                msg = LocalSecretStore.GetErrorMessage(errorCode);
                WriteError(
                    new ErrorRecord(
                        new System.InvalidOperationException(
                            string.Format(CultureInfo.InvariantCulture, "Failed to read secret {0} from vault, with error: {1}", Name, msg)),
                            "SecretWriteError",
                            ErrorCategory.InvalidOperation,
                            this));

                return;
            }

            // Enumerate allowing wildcards in name, used as a filter parameter.
            var filter = Name?? "*";
            if (LocalSecretStore.EnumerateObjects(
                filter,
                All,
                out KeyValuePair<string, object>[] outObjects,
                ref errorCode))
            {
                WriteObject(outObjects);
                return;
            }

            msg = LocalSecretStore.GetErrorMessage(errorCode);
            WriteError(
                new ErrorRecord(
                    new System.InvalidOperationException(
                        string.Format(CultureInfo.InvariantCulture, "Failed to find secrets {0} in vault, with error: {1}", Name, msg)),
                        "SecretWriteError",
                        ErrorCategory.InvalidOperation,
                        this));
        }
    }

    [Cmdlet(VerbsCommon.Remove, "LocalSecret")]
    public sealed class RemoveLocalSecretCommand : PSCmdlet
    {
        [Parameter(Position=0, Mandatory=true)]
        public string Name { get; set; }

        protected override void EndProcessing()
        {
            int errorCode = 0;
            
            if (!LocalSecretStore.DeleteObject(
                Name,
                ref errorCode))
            {
                var msg = LocalSecretStore.GetErrorMessage(errorCode);
                WriteError(
                    new ErrorRecord(
                        new System.InvalidOperationException(
                            string.Format(CultureInfo.InvariantCulture, "Failed to remove secret {0} in vault, with error: {1}", Name, msg)),
                        "SecretRemoveError",
                        ErrorCategory.InvalidOperation,
                        this));
            }
        }
    }

    #endregion
}

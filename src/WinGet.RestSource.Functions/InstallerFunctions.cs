// -----------------------------------------------------------------------
// <copyright file="InstallerFunctions.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Microsoft.WinGet.RestSource.Functions
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.WinGet.RestSource.Common;
    using Microsoft.WinGet.RestSource.Constants;
    using Microsoft.WinGet.RestSource.Cosmos;
    using Microsoft.WinGet.RestSource.Exceptions;
    using Microsoft.WinGet.RestSource.Functions.Constants;
    using Microsoft.WinGet.RestSource.Models;
    using Microsoft.WinGet.RestSource.Models.Errors;
    using Microsoft.WinGet.RestSource.Models.ExtendedSchemas;
    using Microsoft.WinGet.RestSource.Models.Schemas;
    using Newtonsoft.Json;

    /// <summary>
    /// This class contains the functions for interacting with installers.
    /// </summary>
    /// TODO: Refactor duplicate code to library.
    public class InstallerFunctions
    {
        private readonly ICosmosDatabase cosmosDatabase;

        /// <summary>
        /// Initializes a new instance of the <see cref="InstallerFunctions"/> class.
        /// </summary>
        /// <param name="cosmosDatabase">Cosmos Database.</param>
        public InstallerFunctions(ICosmosDatabase cosmosDatabase)
        {
            this.cosmosDatabase = cosmosDatabase;
        }

        /// <summary>
        /// Installer Post Function.
        /// This allows us to make post requests for installers.
        /// </summary>
        /// <param name="req">HttpRequest.</param>
        /// <param name="id">Package ID.</param>
        /// <param name="version">Version ID.</param>
        /// <param name="log">ILogger.</param>
        /// <returns>IActionResult.</returns>
        [FunctionName(FunctionConstants.InstallerPost)]
        public async Task<IActionResult> InstallerPostAsync(
            [HttpTrigger(AuthorizationLevel.Function, FunctionConstants.FunctionPost, Route = "packages/{id}/versions/{version}/installers")]
            HttpRequest req,
            string id,
            string version,
            ILogger log)
        {
            Installer installerCore = null;

            try
            {
                // Parse body as package
                installerCore = await Parser.StreamParser<Installer>(req.Body, log);
                ApiDataValidator.Validate<Installer>(installerCore);

                // Fetch Current Package
                CosmosDocument<CosmosManifest> cosmosDocument = await this.cosmosDatabase.GetByIdAndPartitionKey<CosmosManifest>(id, id);
                log.LogInformation(JsonConvert.SerializeObject(cosmosDocument, Formatting.Indented));

                // Validate Package Version is not null
                if (cosmosDocument.Document.Versions == null)
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.VersionsIsNullErrorCode,
                            ErrorConstants.VersionsIsNullErrorMessage));
                }

                // Validate Version exists
                if (cosmosDocument.Document.Versions.All(versionExtended => versionExtended.PackageVersion != version))
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.VersionDoesNotExistErrorCode,
                            ErrorConstants.VersionDoesNotExistErrorMessage));
                }

                // Get version
                VersionExtended versionToUpdate = cosmosDocument.Document.Versions.First(versionExtended => versionExtended.PackageVersion == version);

                // Create Installers if null
                versionToUpdate.Installers ??= new Installers();

                // If does not exist add
                if (versionToUpdate.Installers.All(nested => nested.InstallerIdentifier != installerCore.InstallerIdentifier))
                {
                    versionToUpdate.Installers.Add(installerCore);
                }
                else
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.InstallerAlreadyExistsErrorCode,
                            ErrorConstants.InstallerAlreadyExistsErrorMessage));
                }

                // Replace Version
                cosmosDocument.Document.Versions = new VersionsExtended(cosmosDocument.Document.Versions.Where(versionExtended => versionExtended.PackageVersion != version));
                cosmosDocument.Document.Versions ??= new VersionsExtended();
                cosmosDocument.Document.Versions.Add(versionToUpdate);

                // Save Document
                await this.cosmosDatabase.Update<CosmosManifest>(cosmosDocument);
            }
            catch (DefaultException e)
            {
                log.LogError(e.ToString());
                return ActionResultHelper.ProcessError(e.InternalRestError);
            }
            catch (Exception e)
            {
                log.LogError(e.ToString());
                return ActionResultHelper.UnhandledError(e);
            }

            return new OkObjectResult(JsonConvert.SerializeObject(installerCore, Formatting.Indented));
        }

        /// <summary>
        /// Installer Delete Function.
        /// This allows us to make delete requests for versions.
        /// </summary>
        /// <param name="req">HttpRequest.</param>
        /// <param name="id">Package ID.</param>
        /// <param name="version">Version ID.</param>
        /// <param name="sha256">SHA 256 for the installer.</param>
        /// <param name="log">ILogger.</param>
        /// <returns>IActionResult.</returns>
        [FunctionName(FunctionConstants.InstallerDelete)]
        public async Task<IActionResult> InstallerDeleteAsync(
            [HttpTrigger(
                AuthorizationLevel.Function,
                FunctionConstants.FunctionDelete,
                Route = "packages/{id}/versions/{version}/installers/{sha256}")]
            HttpRequest req,
            string id,
            string version,
            string sha256,
            ILogger log)
        {
            try
            {
                // Fetch Current Package
                CosmosDocument<CosmosManifest> cosmosDocument = await this.cosmosDatabase.GetByIdAndPartitionKey<CosmosManifest>(id, id);
                log.LogInformation(JsonConvert.SerializeObject(cosmosDocument, Formatting.Indented));

                // Validate Package Version is not null
                if (cosmosDocument.Document.Versions == null)
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.VersionsIsNullErrorCode,
                            ErrorConstants.VersionsIsNullErrorMessage));
                }

                // Validate Version exists
                if (cosmosDocument.Document.Versions.All(versionExtended => versionExtended.PackageVersion != version))
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.VersionDoesNotExistErrorCode,
                            ErrorConstants.VersionDoesNotExistErrorMessage));
                }

                // Get version
                VersionExtended versionToUpdate = cosmosDocument.Document.Versions.First(versionExtended => versionExtended.PackageVersion == version);

                // Validate Installer not null
                if (versionToUpdate.Installers == null)
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.InstallerIsNullErrorCode,
                            ErrorConstants.InstallerIsNullErrorMessage));
                }

                // Verify Installer Exists
                if (versionToUpdate.Installers.All(installer => installer.InstallerIdentifier != sha256))
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.InstallerDoesNotExistErrorCode,
                            ErrorConstants.InstallerDoesNotExistErrorMessage));
                }

                // Remove installer
                versionToUpdate.Installers = new Installers(versionToUpdate.Installers.Where(installer => installer.InstallerIdentifier != sha256));

                // Replace Version
                cosmosDocument.Document.Versions = new VersionsExtended(cosmosDocument.Document.Versions.Where(versionExtended => versionExtended.PackageVersion != version));
                cosmosDocument.Document.Versions ??= new VersionsExtended();
                cosmosDocument.Document.Versions.Add(versionToUpdate);

                // Save Document
                await this.cosmosDatabase.Update<CosmosManifest>(cosmosDocument);
            }
            catch (DefaultException e)
            {
                log.LogError(e.ToString());
                return ActionResultHelper.ProcessError(e.InternalRestError);
            }
            catch (Exception e)
            {
                log.LogError(e.ToString());
                return ActionResultHelper.UnhandledError(e);
            }

            return new NoContentResult();
        }

        /// <summary>
        /// Installer Put Function.
        /// This allows us to make put requests for installers.
        /// </summary>
        /// <param name="req">HttpRequest.</param>
        /// <param name="id">Package ID.</param>
        /// <param name="version">Version ID.</param>
        /// <param name="sha256">SHA 256 for the installer.</param>
        /// <param name="log">ILogger.</param>
        /// <returns>IActionResult.</returns>
        [FunctionName(FunctionConstants.InstallerPut)]
        public async Task<IActionResult> InstallerPutAsync(
            [HttpTrigger(
                AuthorizationLevel.Function,
                FunctionConstants.FunctionPut,
                Route = "packages/{id}/versions/{version}/installers/{sha256}")]
            HttpRequest req,
            string id,
            string version,
            string sha256,
            ILogger log)
        {
            Installer installerCore = null;

            try
            {
                // Parse body as package
                installerCore = await Parser.StreamParser<Installer>(req.Body, log);
                ApiDataValidator.Validate<Installer>(installerCore);

                // Validate Parsed Values
                // TODO: Validate Parsed Values
                if (installerCore.InstallerIdentifier != sha256)
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.InstallerDoesNotMatchErrorCode,
                            ErrorConstants.InstallerDoesNotMatchErrorMessage));
                }

                // Fetch Current Package
                CosmosDocument<CosmosManifest> cosmosDocument =
                    await this.cosmosDatabase.GetByIdAndPartitionKey<CosmosManifest>(id, id);
                log.LogInformation(JsonConvert.SerializeObject(cosmosDocument, Formatting.Indented));

                // Validate Package Version is not null
                if (cosmosDocument.Document.Versions == null)
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.VersionsIsNullErrorCode,
                            ErrorConstants.VersionsIsNullErrorMessage));
                }

                // Validate Version exists
                if (cosmosDocument.Document.Versions.All(versionExtended => versionExtended.PackageVersion != version))
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.VersionDoesNotExistErrorCode,
                            ErrorConstants.VersionDoesNotExistErrorMessage));
                }

                // Get version
                VersionExtended versionToUpdate = cosmosDocument.Document.Versions.First(versionExtended => versionExtended.PackageVersion == version);

                // Validate Installer not null
                if (versionToUpdate.Installers == null)
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.InstallerIsNullErrorCode,
                            ErrorConstants.InstallerIsNullErrorMessage));
                }

                // Verify Installer Exists
                if (versionToUpdate.Installers.All(installer => installer.InstallerIdentifier != sha256))
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.InstallerDoesNotExistErrorCode,
                            ErrorConstants.InstallerDoesNotExistErrorMessage));
                }

                // Replace installer
                versionToUpdate.Installers = new Installers(versionToUpdate.Installers.Where(installer => installer.InstallerIdentifier != sha256));
                versionToUpdate.Installers ??= new Installers();
                versionToUpdate.Installers.Add(installerCore);

                // Replace Version
                cosmosDocument.Document.Versions = new VersionsExtended(cosmosDocument.Document.Versions.Where(versionExtended => versionExtended.PackageVersion != version));
                cosmosDocument.Document.Versions ??= new VersionsExtended();
                cosmosDocument.Document.Versions.Add(versionToUpdate);

                // Save Document
                await this.cosmosDatabase.Update<CosmosManifest>(cosmosDocument);
            }
            catch (DefaultException e)
            {
                log.LogError(e.ToString());
                return ActionResultHelper.ProcessError(e.InternalRestError);
            }
            catch (Exception e)
            {
                log.LogError(e.ToString());
                return ActionResultHelper.UnhandledError(e);
            }

            return new OkObjectResult(JsonConvert.SerializeObject(installerCore, Formatting.Indented));
        }

        /// <summary>
        /// Installer Put Function.
        /// This allows us to make put requests for installers.
        /// </summary>
        /// <param name="req">HttpRequest.</param>
        /// <param name="id">Package ID.</param>
        /// <param name="version">Version ID.</param>
        /// <param name="sha256">SHA 256 for the installer.</param>
        /// <param name="log">ILogger.</param>
        /// <returns>IActionResult.</returns>
        [FunctionName(FunctionConstants.InstallerGet)]
        public async Task<IActionResult> InstallerGetAsync(
            [HttpTrigger(
                AuthorizationLevel.Function,
                FunctionConstants.FunctionGet,
                Route = "packages/{id}/versions/{version}/installers/{sha256?}")]
            HttpRequest req,
            string id,
            string version,
            string sha256,
            ILogger log)
        {
            ApiResponse<Installer> apiResponse = new ApiResponse<Installer>();

            try
            {
                // Fetch Current Package
                CosmosDocument<CosmosManifest> cosmosDocument =
                    await this.cosmosDatabase.GetByIdAndPartitionKey<CosmosManifest>(id, id);
                log.LogInformation(JsonConvert.SerializeObject(cosmosDocument, Formatting.Indented));

                // Validate Package Version is not null
                if (cosmosDocument.Document.Versions == null)
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.VersionsIsNullErrorCode,
                            ErrorConstants.VersionsIsNullErrorMessage));
                }

                // Validate Version exists
                if (cosmosDocument.Document.Versions.All(versionExtended => versionExtended.PackageVersion != version))
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.VersionDoesNotExistErrorCode,
                            ErrorConstants.VersionDoesNotExistErrorMessage));
                }

                // Get version
                VersionExtended versionToUpdate = cosmosDocument.Document.Versions.First(versionExtended => versionExtended.PackageVersion == version);

                // Validate Installer not null
                if (versionToUpdate.Installers == null)
                {
                    throw new InvalidArgumentException(
                        new InternalRestError(
                            ErrorConstants.InstallerIsNullErrorCode,
                            ErrorConstants.InstallerIsNullErrorMessage));
                }

                // Process Installers
                if (string.IsNullOrWhiteSpace(sha256))
                {
                    Installers enumerable = versionToUpdate.Installers;
                    foreach (Installer installerCore in enumerable)
                    {
                        apiResponse.Data.Add(installerCore);
                    }
                }
                else
                {
                    // Verify Installer Exists
                    if (versionToUpdate.Installers.All(installer => installer.InstallerIdentifier != sha256))
                    {
                        throw new InvalidArgumentException(
                            new InternalRestError(
                                ErrorConstants.InstallerDoesNotExistErrorCode,
                                ErrorConstants.InstallerDoesNotExistErrorMessage));
                    }

                    // Add Installer(s)
                    IEnumerable<Installer> enumerable = versionToUpdate.Installers
                        .Where(installer => installer.InstallerIdentifier == sha256);
                    foreach (Installer installerCore in enumerable)
                    {
                        apiResponse.Data.Add(installerCore);
                    }
                }
            }
            catch (DefaultException e)
            {
                log.LogError(e.ToString());
                return ActionResultHelper.ProcessError(e.InternalRestError);
            }
            catch (Exception e)
            {
                log.LogError(e.ToString());
                return ActionResultHelper.UnhandledError(e);
            }

            return apiResponse.Data.Count switch
            {
                0 => new NoContentResult(),
                _ => new OkObjectResult(JsonConvert.SerializeObject(apiResponse, Formatting.Indented))
            };
        }
    }
}

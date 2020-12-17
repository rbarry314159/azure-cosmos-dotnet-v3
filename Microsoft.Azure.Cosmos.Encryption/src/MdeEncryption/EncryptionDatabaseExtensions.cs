﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Encryption
{
    using System.Threading.Tasks;
    using Microsoft.Data.Encryption.Cryptography;

    /// <summary>
    /// This class provides extension methods for <see cref="Database"/>.
    /// </summary>
    public static class EncryptionDatabaseExtensions
    {
        /// <summary>
        /// Create a Client Encryption Key used to Encrypt data.
        /// </summary>
        /// <param name="database">Regular cosmos database.</param>
        /// <param name="clientEncryptionKeyId"> Client Encryption Key id.</param>
        /// <param name="encryptionAlgorithm"> Encryption Algorthm. </param>
        /// <param name="encryptionKeyWrapMetadata"> EncryptionKeyWrapMetadata.</param>
        /// <returns>Container to perform operations supporting client-side encryption / decryption.</returns>
        public static async Task<ClientEncryptionKeyResponse> CreateClientEncryptionKeyAsync(
            this Database database,
            string clientEncryptionKeyId,
            string encryptionAlgorithm,
            Cosmos.EncryptionKeyWrapMetadata encryptionKeyWrapMetadata)
        {
            ClientEncryptionKey clientEncryptionKey = database.GetClientEncryptionKey(clientEncryptionKeyId);

            EncryptionCosmosClient encryptionCosmosClient = (EncryptionCosmosClient)database.Client;
            EncryptionKeyStoreProvider encryptionKeyStoreProvider = encryptionCosmosClient.EncryptionKeyStoreProvider;

            KeyEncryptionKey keyEncryptionKey = KeyEncryptionKey.GetOrCreate(
                encryptionKeyWrapMetadata.Value,
                encryptionKeyWrapMetadata.Value,
                encryptionKeyStoreProvider);

            ProtectedDataEncryptionKey protectedDataEncryptionKey = new ProtectedDataEncryptionKey(
                encryptionKeyWrapMetadata.Value,
                keyEncryptionKey);

            byte[] wrappedDataEncryptionKey = protectedDataEncryptionKey.EncryptedValue;

            ClientEncryptionKeyProperties clientEncryptionKeyProperties = new ClientEncryptionKeyProperties(
                clientEncryptionKeyId,
                encryptionAlgorithm,
                wrappedDataEncryptionKey,
                encryptionKeyWrapMetadata);

            ClientEncryptionKeyResponse clientEncryptionKeyResponse = await database.CreateClientEncryptionKeyAsync(
                clientEncryptionKey,
                clientEncryptionKeyProperties);

            return clientEncryptionKeyResponse;
        }

        /// <summary>
        /// ReWrap an existing Client Encryption Key.
        /// </summary>
        /// <param name="database">Regular cosmos database.</param>
        /// <param name="clientEncryptionKeyId"> Client Encryption Key id.</param>
        /// <param name="encryptionAlgorithm"> Encryption Algorthm. </param>
        /// <param name="encryptionKeyWrapMetadata"> EncryptionKeyWrapMetadata.</param>
        /// <returns>Container to perform operations supporting client-side encryption / decryption.</returns>
        public static async Task<ClientEncryptionKeyResponse> RewrapClientEncryptionKeyAsync(
            this Database database,
            string clientEncryptionKeyId,
            string encryptionAlgorithm,
            Cosmos.EncryptionKeyWrapMetadata encryptionKeyWrapMetadata)
        {
            ClientEncryptionKey clientEncryptionKey = database.GetClientEncryptionKey(clientEncryptionKeyId);

            EncryptionCosmosClient encryptionCosmosClient = (EncryptionCosmosClient)database.Client;
            EncryptionKeyStoreProvider encryptionKeyStoreProvider = encryptionCosmosClient.EncryptionKeyStoreProvider;

            ClientEncryptionKeyProperties clientEncryptionKeyProperties = await clientEncryptionKey.ReadAsync();

            KeyEncryptionKey keyEncryptionKey = KeyEncryptionKey.GetOrCreate(
                encryptionKeyWrapMetadata.Value,
                encryptionKeyWrapMetadata.Value,
                encryptionKeyStoreProvider);

            byte[] unWrappedKey = keyEncryptionKey.DecryptEncryptionKey(clientEncryptionKeyProperties.WrappedDataEncryptionKey);

            byte[] reWrappedKey = keyEncryptionKey.EncryptEncryptionKey(unWrappedKey);

            clientEncryptionKeyProperties = new ClientEncryptionKeyProperties(
                clientEncryptionKeyId,
                encryptionAlgorithm,
                reWrappedKey,
                encryptionKeyWrapMetadata);

            ClientEncryptionKeyResponse clientEncryptionKeyResponse = await database.ReplaceClientEncryptionKeyAsync(
                clientEncryptionKey,
                clientEncryptionKeyProperties);

            return clientEncryptionKeyResponse;
        }
    }
}
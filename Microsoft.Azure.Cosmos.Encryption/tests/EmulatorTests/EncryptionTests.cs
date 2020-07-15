﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Encryption.EmulatorTests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Cosmos.Encryption;
    using Microsoft.Azure.Cosmos.Scripts;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    [TestClass]
    public class EncryptionTests
    {
        private static readonly EncryptionKeyWrapMetadata metadata1 = new EncryptionKeyWrapMetadata("metadata1");
        private static readonly EncryptionKeyWrapMetadata metadata2 = new EncryptionKeyWrapMetadata("metadata2");
        private const string metadataUpdateSuffix = "updated";
        private static TimeSpan cacheTTL = TimeSpan.FromDays(1);
        private const string dekId = "mydek";
        private const string pdekId = "mypdek";
        private const string pdekId2 = "mypdek2";
        private static CosmosClient client;
        private static Database database;
        private static DataEncryptionKeyProperties dekProperties;
        private static DataEncryptionKeyProperties pdekProperties_1;
        private static DataEncryptionKeyProperties pdekProperties_2;
        private static Container itemContainer;
        private static Container encryptionContainer;
        private static Container propertyEncryptionContainer;
        private static Container multipropertymultidek;
        private static Container multipropertysingledek;
        private static Container keyContainer;
        private static CosmosDataEncryptionKeyProvider dekProvider;
        private static TestEncryptor encryptor;
        private static string decryptionFailedDocId;
        private static Dictionary<List<string>, string> PathsToEncrypt = new Dictionary<List<string>, string> { { TestDoc.PropertyPathsToEncrypt, pdekId } };
        private static Dictionary<List<string>, string> PathsToEncryptMultiDek = new Dictionary<List<string>, string> { { TestDoc.PropertyPathsToEncrypt, pdekId }, { TestDoc.PropertyPathsToEncrypt2, pdekId2 } };
        private static Dictionary<List<string>, string> PathsToEncryptSingleDek = new Dictionary<List<string>, string> { { TestDoc.Property2PathsToEncrypt, pdekId2 } };

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            EncryptionTests.dekProvider = new CosmosDataEncryptionKeyProvider(new TestKeyWrapProvider());
            EncryptionTests.encryptor = new TestEncryptor(EncryptionTests.dekProvider);

            EncryptionTests.client = TestCommon.CreateCosmosClient();
            EncryptionTests.database = await EncryptionTests.client.CreateDatabaseAsync(Guid.NewGuid().ToString());

            EncryptionTests.keyContainer = await EncryptionTests.database.CreateContainerAsync(Guid.NewGuid().ToString(), "/id", 400);
            await EncryptionTests.dekProvider.InitializeAsync(EncryptionTests.database, EncryptionTests.keyContainer.Id);

            EncryptionTests.itemContainer = await EncryptionTests.database.CreateContainerAsync(Guid.NewGuid().ToString(), "/PK", 400);
            EncryptionTests.encryptionContainer = EncryptionTests.itemContainer.WithEncryptor(encryptor);
            EncryptionTests.dekProperties = await EncryptionTests.CreateDekAsync(EncryptionTests.dekProvider, EncryptionTests.dekId);

            EncryptionTests.propertyEncryptionContainer = EncryptionTests.itemContainer.WithPropertyEncryptor(encryptor, EncryptionTests.PathsToEncrypt);
            EncryptionTests.pdekProperties_2 = await EncryptionTests.CreatePropertyDekAsync(EncryptionTests.dekProvider, EncryptionTests.pdekId);
            EncryptionTests.pdekProperties_1 = await EncryptionTests.CreatePropertyDekAsync(EncryptionTests.dekProvider, EncryptionTests.pdekId2);

            EncryptionTests.multipropertymultidek = EncryptionTests.itemContainer.WithPropertyEncryptor(encryptor, EncryptionTests.PathsToEncryptMultiDek);
            EncryptionTests.multipropertysingledek = EncryptionTests.itemContainer.WithPropertyEncryptor(encryptor, EncryptionTests.PathsToEncryptSingleDek);

        }

        [ClassCleanup]
        public static async Task ClassCleanup()
        {
            if (EncryptionTests.database != null)
            {
                using (await EncryptionTests.database.DeleteStreamAsync()) { }
            }

            if (EncryptionTests.client != null)
            {
                EncryptionTests.client.Dispose();
            }
        }

        [TestMethod]
        public async Task EncryptionCreateDek()
        {
            string dekId = "anotherDek";
            DataEncryptionKeyProperties dekProperties = await EncryptionTests.CreateDekAsync(EncryptionTests.dekProvider, dekId);
            
            Assert.AreEqual(
                new EncryptionKeyWrapMetadata(EncryptionTests.metadata1.Value + EncryptionTests.metadataUpdateSuffix),
                dekProperties.EncryptionKeyWrapMetadata);

            // Use different DEK provider to avoid (unintentional) cache impact
            CosmosDataEncryptionKeyProvider dekProvider = new CosmosDataEncryptionKeyProvider(new TestKeyWrapProvider());
            await dekProvider.InitializeAsync(EncryptionTests.database, EncryptionTests.keyContainer.Id);
            DataEncryptionKeyProperties readProperties = await dekProvider.DataEncryptionKeyContainer.ReadDataEncryptionKeyAsync(dekId);
            Assert.AreEqual(dekProperties, readProperties);
        }

        [TestMethod]
        public async Task EncryptionRewrapDek()
        {
            string dekId = "randomDek";
            DataEncryptionKeyProperties dekProperties = await EncryptionTests.CreateDekAsync(EncryptionTests.dekProvider, dekId);
            Assert.AreEqual(
                new EncryptionKeyWrapMetadata(EncryptionTests.metadata1.Value + EncryptionTests.metadataUpdateSuffix),
                dekProperties.EncryptionKeyWrapMetadata);

            ItemResponse<DataEncryptionKeyProperties> dekResponse = await EncryptionTests.dekProvider.DataEncryptionKeyContainer.RewrapDataEncryptionKeyAsync(
                dekId,
                EncryptionTests.metadata2);

            Assert.AreEqual(HttpStatusCode.OK, dekResponse.StatusCode);
            dekProperties = EncryptionTests.VerifyDekResponse(
                dekResponse,
                dekId);
            Assert.AreEqual(
                new EncryptionKeyWrapMetadata(EncryptionTests.metadata2.Value + EncryptionTests.metadataUpdateSuffix),
                dekProperties.EncryptionKeyWrapMetadata);

            // Use different DEK provider to avoid (unintentional) cache impact
            CosmosDataEncryptionKeyProvider dekProvider = new CosmosDataEncryptionKeyProvider(new TestKeyWrapProvider());
            await dekProvider.InitializeAsync(EncryptionTests.database, EncryptionTests.keyContainer.Id);
            DataEncryptionKeyProperties readProperties = await dekProvider.DataEncryptionKeyContainer.ReadDataEncryptionKeyAsync(dekId);
            Assert.AreEqual(dekProperties, readProperties);
        }

        [TestMethod]
        public async Task EncryptionDekReadFeed()
        {
            Container newKeyContainer = await EncryptionTests.database.CreateContainerAsync(Guid.NewGuid().ToString(), "/id", 400);
            try
            {
                CosmosDataEncryptionKeyProvider dekProvider = new CosmosDataEncryptionKeyProvider(new TestKeyWrapProvider());
                await dekProvider.InitializeAsync(EncryptionTests.database, newKeyContainer.Id);

                string contosoV1 = "Contoso_v001";
                string contosoV2 = "Contoso_v002";
                string fabrikamV1 = "Fabrikam_v001";
                string fabrikamV2 = "Fabrikam_v002";

                await EncryptionTests.CreateDekAsync(dekProvider, contosoV1);
                await EncryptionTests.CreateDekAsync(dekProvider, contosoV2);
                await EncryptionTests.CreateDekAsync(dekProvider, fabrikamV1);
                await EncryptionTests.CreateDekAsync(dekProvider, fabrikamV2);

                // Test getting all keys
                await EncryptionTests.IterateDekFeedAsync(
                    dekProvider,
                    new List<string> { contosoV1, contosoV2, fabrikamV1, fabrikamV2 },
                    isExpectedDeksCompleteSetForRequest: true,
                    isResultOrderExpected: false,
                    "SELECT * from c");

                // Test getting specific subset of keys
                await EncryptionTests.IterateDekFeedAsync(
                    dekProvider,
                    new List<string> { contosoV2 },
                    isExpectedDeksCompleteSetForRequest: false,
                    isResultOrderExpected: true,
                    "SELECT TOP 1 * from c where c.id >= 'Contoso_v000' and c.id <= 'Contoso_v999' ORDER BY c.id DESC");

                // Ensure only required results are returned
                await EncryptionTests.IterateDekFeedAsync(
                    dekProvider,
                    new List<string> { contosoV1, contosoV2 },
                    isExpectedDeksCompleteSetForRequest: true,
                    isResultOrderExpected: true,
                    "SELECT * from c where c.id >= 'Contoso_v000' and c.id <= 'Contoso_v999' ORDER BY c.id ASC");

                // Test pagination
                await EncryptionTests.IterateDekFeedAsync(
                    dekProvider,
                    new List<string> { contosoV1, contosoV2, fabrikamV1, fabrikamV2 },
                    isExpectedDeksCompleteSetForRequest: true,
                    isResultOrderExpected: false,
                    "SELECT * from c",
                    itemCountInPage: 3);
            }
            finally
            {
                await newKeyContainer.DeleteContainerStreamAsync();
            }
        }

        [TestMethod]
        public async Task EncryptionCreateItemWithoutEncryptionOptions()
        {
            TestDoc testDoc = TestDoc.Create();
            ItemResponse<TestDoc> createResponse = await EncryptionTests.encryptionContainer.CreateItemAsync(
                testDoc,
                new PartitionKey(testDoc.PK));
            Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
            Assert.AreEqual(testDoc, createResponse.Resource);
        }

        [TestMethod]
        public async Task EncryptionCreateItemWithNullEncryptionOptions()
        {
            TestDoc testDoc = TestDoc.Create();
            ItemResponse<TestDoc> createResponse = await EncryptionTests.encryptionContainer.CreateItemAsync(
                testDoc,
                new PartitionKey(testDoc.PK),
                new EncryptionItemRequestOptions());
            Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
            Assert.AreEqual(testDoc, createResponse.Resource);
        }

        [TestMethod]
        public async Task EncryptionCreateItemWithoutPartitionKey()
        {
            TestDoc testDoc = TestDoc.Create();
            try
            {
                await EncryptionTests.encryptionContainer.CreateItemAsync(
                    testDoc,
                    requestOptions: EncryptionTests.GetRequestOptions(EncryptionTests.dekId, TestDoc.PathsToEncrypt));
                Assert.Fail("CreateItem should've failed because PartitionKey was not provided.");
            }
            catch (NotSupportedException ex)
            {
                Assert.AreEqual("partitionKey cannot be null for operations using EncryptionContainer.", ex.Message);
            }
        }

        [TestMethod]
        public async Task EncryptionFailsWithUnknownDek()
        {
            string unknownDek = "unknownDek";

            try
            {
                await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, unknownDek, TestDoc.PathsToEncrypt);
            }
            catch (ArgumentException ex)
            {
                Assert.AreEqual($"Failed to retrieve Data Encryption Key with id: '{unknownDek}'.", ex.Message);
                Assert.IsTrue(ex.InnerException is CosmosException);
            }
        }


        [TestMethod]
        public async Task CreateItemWithPropertyEncr()
        {
            TestDoc testDoc = TestDoc.Create();

            ItemResponse<TestDoc> createResponse = await EncryptionTests.propertyEncryptionContainer.CreateItemAsync(
                testDoc, new PartitionKey(testDoc.PK));

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.propertyEncryptionContainer, testDoc);

            await EncryptionTests.VerifyItemByReadStreamAsync(EncryptionTests.propertyEncryptionContainer, testDoc);

            TestDoc expectedDoc = new TestDoc(testDoc);

            // Read feed (null query)
            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.propertyEncryptionContainer,
                 query: null,
                 expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.propertyEncryptionContainer,
                 "SELECT * FROM c",
                 expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                string.Format(
                    "SELECT * FROM c where c.Name = {0}",
                    expectedDoc.Name),
                    expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                string.Format(
                    "SELECT * FROM c where c.PK = '{0}' and c.id = '{1}'",
                    expectedDoc.PK,
                    expectedDoc.Id),
                expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.Name = @theName")
                         .WithParameter("@theName", expectedDoc.Name),
                expectedDoc: expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.Name = @Name")
                         .WithParameter("@Name", expectedDoc.Name),
                expectedDoc: expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.id = @theId and c.PK = @thePK")
                         .WithParameter("@theId", expectedDoc.Id)
                         .WithParameter("@thePK", expectedDoc.PK),
                expectedDoc: expectedDoc);


            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.propertyEncryptionContainer,
                 "SELECT c.id, c.PK, c.Name,c.City, c.Sensitive, c.NonSensitive, c.SSN FROM c",
                 expectedDoc);

        }

        [TestMethod]
        public async Task CreateStreamItemWithPropertyEncr()
        {
            TestDoc testDoc = TestDoc.Create();
            Stream testStream = testDoc.ToStream();

            await EncryptionTests.propertyEncryptionContainer.CreateItemStreamAsync(
                testStream, new PartitionKey(testDoc.PK));

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.propertyEncryptionContainer, testDoc);

            await EncryptionTests.VerifyItemByReadStreamAsync(EncryptionTests.propertyEncryptionContainer, testDoc);

            TestDoc expectedDoc = new TestDoc(testDoc);


            // Read feed (null query)
            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.propertyEncryptionContainer,
                 query: null,
                 expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.propertyEncryptionContainer,
                 "SELECT * FROM c",
                 expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                string.Format(
                    "SELECT * FROM c where c.Name = {0}",
                    expectedDoc.Name),
                    expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                string.Format(
                    "SELECT * FROM c where c.PK = '{0}' and c.id = '{1}'",
                    expectedDoc.PK,
                    expectedDoc.Id),
                expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.Name = @theName")
                         .WithParameter("@theName", expectedDoc.Name),
                expectedDoc: expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.id = @theId and c.PK = @thePK")
                         .WithParameter("@theId", expectedDoc.Id)
                         .WithParameter("@thePK", expectedDoc.PK),
                expectedDoc: expectedDoc);


            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.propertyEncryptionContainer,
                 "SELECT c.id, c.PK, c.Name,c.City,c.SSN, c.Sensitive, c.NonSensitive FROM c",
                 expectedDoc);

        }

        [TestMethod]
        public async Task CreateItemWith2Property2DekEncr()
        {
            TestDoc testDoc = TestDoc.Create();
            ItemResponse<TestDoc> createResponse = await EncryptionTests.multipropertymultidek.CreateItemAsync(
                testDoc, new PartitionKey(testDoc.PK));

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.multipropertymultidek, testDoc);

            await EncryptionTests.VerifyItemByReadStreamAsync(EncryptionTests.multipropertymultidek, testDoc);

            TestDoc expectedDoc = new TestDoc(testDoc);

            // Read feed (null query)
            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.multipropertymultidek,
                 query: null,
                 expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.multipropertymultidek,
                 "SELECT * FROM c",
                 expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.multipropertymultidek,
                 string.Format(
                     "SELECT * FROM c where c.Name = {0}",
                     expectedDoc.Name),
                     expectedDoc);
            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.multipropertymultidek,
                 string.Format(
                     "SELECT * FROM c where c.City = {0}",
                     expectedDoc.City),
                     expectedDoc);
            // FIX
#if true
            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.multipropertymultidek,
                string.Format(
                    "SELECT * FROM c where c.id = '{0}' and c.City = {1}",
                    expectedDoc.Id,
                    expectedDoc.City),
                expectedDoc);
#endif
           
            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.multipropertymultidek,
                string.Format(
                    "SELECT * FROM c where c.PK = '{0}' and c.id = '{1}'",
                    expectedDoc.PK,
                    expectedDoc.Id),
                expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
               EncryptionTests.multipropertymultidek,
               queryDefinition: new QueryDefinition(
                   "select * from c where c.City = @theCity")
                        .WithParameter("@theCity", expectedDoc.City),
               expectedDoc: expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
               EncryptionTests.multipropertymultidek,
               queryDefinition: new QueryDefinition(
                   "select * from c where c.Name = @theName")
                        .WithParameter("@theName", expectedDoc.Name),
               expectedDoc: expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.multipropertymultidek,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.id = @theId and c.PK = @thePK")
                         .WithParameter("@theId", expectedDoc.Id)
                         .WithParameter("@thePK", expectedDoc.PK),
                expectedDoc: expectedDoc);

            // FIX
#if true
            await EncryptionTests.ValidateQueryResultsAsync(
               EncryptionTests.multipropertymultidek,
               queryDefinition: new QueryDefinition(
                   "select * from c where c.id = @theId and c.SSN = @theSSN")
                        .WithParameter("@theId", expectedDoc.Id)
                        .WithParameter("@theSSN", expectedDoc.SSN),
               expectedDoc: expectedDoc);
#endif
            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.multipropertymultidek,
                 "SELECT c.id, c.PK, c.Name,c.City,c.SSN, c.Sensitive, c.NonSensitive FROM c",
                 expectedDoc);

        }

        [TestMethod]
        public async Task CreateItemWith2Property1DekEncr()
        {
            TestDoc testDoc = TestDoc.Create();
            ItemResponse<TestDoc> createResponse = await EncryptionTests.multipropertysingledek.CreateItemAsync(
                testDoc, new PartitionKey(testDoc.PK));

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.multipropertysingledek, testDoc);

            await EncryptionTests.VerifyItemByReadStreamAsync(EncryptionTests.multipropertysingledek, testDoc);

            TestDoc expectedDoc = new TestDoc(testDoc);

            // Read feed (null query)
            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.multipropertysingledek,
                 query: null,
                 expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.multipropertysingledek,
                 "SELECT * FROM c",
                 expectedDoc);

#if true
            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.multipropertysingledek,
                string.Format(
                    "SELECT * FROM c where c.id = '{0}' and c.Name= {1} ",
                    expectedDoc.Id, expectedDoc.Name),
                expectedDoc);
#endif    

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.multipropertysingledek,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.Name = @theName")
                         .WithParameter("@theName", expectedDoc.Name),
                expectedDoc: expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.multipropertysingledek,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.SSN = @theSSN")
                         .WithParameter("@theSSN", expectedDoc.SSN),
                expectedDoc: expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.multipropertysingledek,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.SSN = @theSSN and c.Name=@theName")
                         .WithParameter("@theSSN", expectedDoc.SSN)
                         .WithParameter("@theName", expectedDoc.Name),
                expectedDoc: expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.multipropertysingledek,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.id = @theId and c.PK = @thePK")
                         .WithParameter("@theId", expectedDoc.Id)
                         .WithParameter("@thePK", expectedDoc.PK),
                expectedDoc: expectedDoc);


            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.multipropertysingledek,
                 "SELECT c.id, c.PK, c.Name,c.City,c.SSN, c.Sensitive, c.NonSensitive FROM c",
                 expectedDoc);
        }

        [TestMethod]
        public async Task EncryptionCreateItemWithPropertyEncr()
        {
            TestDoc testDoc = await CreateItemAsync(EncryptionTests.propertyEncryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.propertyEncryptionContainer, testDoc);

            await EncryptionTests.VerifyItemByReadStreamAsync(EncryptionTests.propertyEncryptionContainer, testDoc);

            TestDoc expectedDoc = new TestDoc(testDoc);

            // Read feed (null query)
            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                query: null,
                expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                "SELECT * FROM c",
                expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                 string.Format(
                     "SELECT * FROM c where c.Name = {0}",
                     expectedDoc.Name),
                     expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
               EncryptionTests.propertyEncryptionContainer,
               string.Format("SELECT * FROM c where c.Sensitive = '{0}'", testDoc.Sensitive),
               expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                string.Format(
                    "SELECT * FROM c where c.PK = '{0}' and c.id = '{1}' and c.NonSensitive = '{2}'",
                    expectedDoc.PK,
                    expectedDoc.Id,
                    expectedDoc.NonSensitive),
                expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.id = @theId and c.PK = @thePK")
                         .WithParameter("@theId", expectedDoc.Id)
                         .WithParameter("@thePK", expectedDoc.PK),
                expectedDoc: expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.propertyEncryptionContainer,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.Name = @theName")
                         .WithParameter("@theName", expectedDoc.Name),
                expectedDoc: expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                 EncryptionTests.propertyEncryptionContainer,
                 "SELECT c.id, c.PK, c.Name, c.City, c.Sensitive, c.NonSensitive, c.SSN FROM c",
                 expectedDoc);

        }

        [TestMethod]
        public async Task PropertyEncrAndEncryptionChangeFeedDecryptionSuccessful()
        {
            string pdek2 = "pdek2ForChangeFeed";
            await EncryptionTests.CreatePropertyDekAsync(EncryptionTests.dekProvider, pdek2);

            TestDoc testDoc1 = TestDoc.Create();
            ItemResponse<TestDoc> createResponse = await EncryptionTests.propertyEncryptionContainer.CreateItemAsync(
                testDoc1, new PartitionKey(testDoc1.PK));

            TestDoc testDoc2 = TestDoc.Create();
            ItemResponse<TestDoc> createResponse2 = await EncryptionTests.propertyEncryptionContainer.CreateItemAsync(
                testDoc2, new PartitionKey(testDoc2.PK));

            // change feed iterator
            await this.ValidateChangeFeedIteratorResponse(EncryptionTests.propertyEncryptionContainer, testDoc1, testDoc2);

            // change feed processor
            // await this.ValidateChangeFeedProcessorResponse(EncryptionTests.propertyEncryptionContainer, testDoc1, testDoc2);
        }

        [TestMethod]
        public async Task RudItemWithPropertyEncryption()
        {
            TestDoc testDoc = TestDoc.Create();
            testDoc = await EncryptionTests.propertyEncryptionContainer.UpsertItemAsync(
                testDoc,
                new PartitionKey(testDoc.PK));

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.propertyEncryptionContainer, testDoc);

            testDoc.NonSensitive = Guid.NewGuid().ToString();
            testDoc.Name = Guid.NewGuid().ToString();

            ItemResponse<TestDoc> upsertResponse = await EncryptionTests.propertyEncryptionContainer.UpsertItemAsync(
                testDoc,
                new PartitionKey(testDoc.PK));
            TestDoc updatedDoc = upsertResponse.Resource;

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.propertyEncryptionContainer, updatedDoc);

            updatedDoc.NonSensitive = Guid.NewGuid().ToString();
            updatedDoc.Name = "NewName";

            TestDoc replacedDoc = await EncryptionTests.propertyEncryptionContainer.ReplaceItemAsync(
                testDoc,
                testDoc.Id,
                new PartitionKey(testDoc.PK));

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.propertyEncryptionContainer, replacedDoc);

            await EncryptionTests.DeleteItemAsync(EncryptionTests.propertyEncryptionContainer, replacedDoc);
        }

        [TestMethod]
        public async Task PropertyEncryptionDistinctQueryValueResponse()
        {
            TestDoc testDoc = TestDoc.Create();

            ItemResponse<TestDoc> createResponse = await EncryptionTests.propertyEncryptionContainer.CreateItemAsync(
                testDoc, new PartitionKey(testDoc.PK));

            string query = "SELECT DISTINCT * FROM c";

            await EncryptionTests.ValidatePropertyEncryptedQueryResponseAsync(EncryptionTests.propertyEncryptionContainer, query);
        }

        [TestMethod]
        public async Task PropertyEncryptionCountQueryValueResponse()
        {
            TestDoc testDoc = TestDoc.Create();

            ItemResponse<TestDoc> createResponse = await EncryptionTests.propertyEncryptionContainer.CreateItemAsync(
                testDoc, new PartitionKey(testDoc.PK));

            string query = "SELECT VALUE COUNT(c.Name) FROM c";

            await EncryptionTests.ValidatePropertyEncryptedQueryResponseAsync(EncryptionTests.propertyEncryptionContainer, query);
        }

        [TestMethod]
        public async Task PropertyEncryptionDecryptGroupByQueryResultTest()
        {
            TestDoc testDoc = TestDoc.Create();
            ItemResponse<TestDoc> createResponse = await EncryptionTests.propertyEncryptionContainer.CreateItemAsync(
                testDoc, new PartitionKey(testDoc.PK));

            TestDoc testDocb = TestDoc.Create();
            ItemResponse<TestDoc> createResponseb = await EncryptionTests.propertyEncryptionContainer.CreateItemAsync(
                testDocb, new PartitionKey(testDocb.PK));

            string query = $"SELECT COUNT(c.Name), c.City " +
                           $"FROM c WHERE c.City = '{"myCity"}' " +
                           $"GROUP BY c.City ";

            await EncryptionTests.ValidatePropertyEncryptedQueryResponseAsync(EncryptionTests.propertyEncryptionContainer, query);

            string queryb = $"SELECT COUNT(c.Id), c.Name " +
                           $"FROM c WHERE c.Name = '{"myName"}' " +
                           $"GROUP BY c.Name ";

            await EncryptionTests.ValidatePropertyEncryptedQueryResponseAsync(EncryptionTests.propertyEncryptionContainer, query);

        }
        [TestMethod]
        public async Task EncryptionCreateItem()
        {
            TestDoc testDoc = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.encryptionContainer, testDoc);

            await EncryptionTests.VerifyItemByReadStreamAsync(EncryptionTests.encryptionContainer, testDoc);

            TestDoc expectedDoc = new TestDoc(testDoc);

            // Read feed (null query)
            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.encryptionContainer,
                query: null,
                expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.encryptionContainer,
                "SELECT * FROM c",
                expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.encryptionContainer,
                string.Format(
                    "SELECT * FROM c where c.PK = '{0}' and c.id = '{1}' and c.NonSensitive = '{2}'",
                    expectedDoc.PK,
                    expectedDoc.Id,
                    expectedDoc.NonSensitive),
                expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.encryptionContainer,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.PK = @thePK")
                         .WithParameter("@thePK", expectedDoc.PK),
                expectedDoc: expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.encryptionContainer,
                queryDefinition: new QueryDefinition(
                    "select * from c where c.id = @theId and c.PK = @thePK")
                         .WithParameter("@theId", expectedDoc.Id)
                         .WithParameter("@thePK", expectedDoc.PK),
                expectedDoc: expectedDoc);

            expectedDoc.Sensitive = null;

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.encryptionContainer,
                "SELECT c.id, c.PK, c.Name, c.City, c.SSN, c.Sensitive, c.NonSensitive FROM c",
                expectedDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.encryptionContainer,
                "SELECT c.id, c.PK, c.Name, c.City, c.SSN, c.NonSensitive FROM c",
                expectedDoc);

            await EncryptionTests.ValidateSprocResultsAsync(
                EncryptionTests.encryptionContainer,
                expectedDoc);
        }

        [TestMethod]
        public async Task EncryptionChangeFeedDecryptionSuccessful()
        {
            string dek2 = "dek2ForChangeFeed";
            await EncryptionTests.CreateDekAsync(EncryptionTests.dekProvider, dek2);

            TestDoc testDoc1 = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);
            TestDoc testDoc2 = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, dek2, TestDoc.PathsToEncrypt);
            
            // change feed iterator
            await this.ValidateChangeFeedIteratorResponse(EncryptionTests.encryptionContainer, testDoc1, testDoc2);

            // change feed processor
            // await this.ValidateChangeFeedProcessorResponse(EncryptionTests.encryptionContainer, testDoc1, testDoc2);
        }

        [TestMethod]
        public async Task EncryptionHandleDecryptionFailure()
        {
            string dek2 = "failDek";
            await EncryptionTests.CreateDekAsync(EncryptionTests.dekProvider, dek2);

            TestDoc testDoc1 = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, dek2, TestDoc.PathsToEncrypt);
            TestDoc testDoc2 = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);

            string query = $"SELECT * FROM c WHERE c.PK in ('{testDoc1.PK}', '{testDoc2.PK}')";

            // success
            await EncryptionTests.ValidateQueryResultsMultipleDocumentsAsync(EncryptionTests.encryptionContainer, testDoc1, testDoc2, query);

            // induce failure
            EncryptionTests.encryptor.FailDecryption = true;
            EncryptionTests.decryptionFailedDocId = testDoc1.Id;
            testDoc1.Sensitive = null;

            await EncryptionTests.VerifyItemByReadAsync(
                EncryptionTests.encryptionContainer,
                testDoc1,
                EncryptionTests.GetItemRequestOptionsWithDecryptionResultHandler());

            await EncryptionTests.VerifyItemByReadStreamAsync(
                EncryptionTests.encryptionContainer,
                testDoc1,
                EncryptionTests.GetItemRequestOptionsWithDecryptionResultHandler());

            EncryptionQueryRequestOptions queryRequestOptions = new EncryptionQueryRequestOptions
            {
                DecryptionResultHandler = EncryptionTests.ErrorHandler
            };

            await EncryptionTests.ValidateQueryResultsMultipleDocumentsAsync(
                EncryptionTests.encryptionContainer,
                testDoc1,
                testDoc2,
                query,
                queryRequestOptions);

            // GetItemLinqQueryable
            await EncryptionTests.ValidateQueryResultsMultipleDocumentsAsync(
                EncryptionTests.encryptionContainer,
                testDoc1,
                testDoc2,
                query: null,
                queryRequestOptions);

            await this.ValidateChangeFeedIteratorResponse(
                EncryptionTests.encryptionContainer,
                testDoc1,
                testDoc2,
                EncryptionTests.ErrorHandler);

            // await this.ValidateChangeFeedProcessorResponse(EncryptionTests.itemContainerCore, testDoc1, testDoc2, false);
            EncryptionTests.encryptor.FailDecryption = false;
        }

        [TestMethod]
        public async Task EncryptionDecryptQueryResultMultipleDocs()
        {
            TestDoc testDoc1 = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);
            TestDoc testDoc2 = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);

            // test GetItemLinqQueryable
            await EncryptionTests.ValidateQueryResultsMultipleDocumentsAsync(EncryptionTests.encryptionContainer, testDoc1, testDoc2, null);

            string query = $"SELECT * FROM c WHERE c.PK in ('{testDoc1.PK}', '{testDoc2.PK}')";
            await EncryptionTests.ValidateQueryResultsMultipleDocumentsAsync(EncryptionTests.encryptionContainer, testDoc1, testDoc2, query);

            // ORDER BY query
            query = query + " ORDER BY c._ts";
            await EncryptionTests.ValidateQueryResultsMultipleDocumentsAsync(EncryptionTests.encryptionContainer, testDoc1, testDoc2, query);
        }

        [TestMethod]
        public async Task EncryptionDecryptQueryResultMultipleEncryptedProperties()
        {
            TestDoc testDoc = await EncryptionTests.CreateItemAsync(
                EncryptionTests.encryptionContainer,
                EncryptionTests.dekId,
                new List<string>() { "/Sensitive", "/NonSensitive" });

            TestDoc expectedDoc = new TestDoc(testDoc);

            await EncryptionTests.ValidateQueryResultsAsync(
                EncryptionTests.encryptionContainer,
                "SELECT * FROM c",
                expectedDoc);
        }

        [TestMethod]
        public async Task EncryptionDecryptQueryValueResponse()
        {
            await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);
            string query = "SELECT VALUE COUNT(1) FROM c";

            await EncryptionTests.ValidateQueryResponseAsync(EncryptionTests.encryptionContainer, query);
        }

        [TestMethod]
        public async Task EncryptionDecryptGroupByQueryResultTest()
        {
            string partitionKey = Guid.NewGuid().ToString();

            await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt, partitionKey);
            await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt, partitionKey);

            string query = $"SELECT COUNT(c.Id), c.PK " +
                           $"FROM c WHERE c.PK = '{partitionKey}' " +
                           $"GROUP BY c.PK ";

            await EncryptionTests.ValidateQueryResponseAsync(EncryptionTests.encryptionContainer, query);
        }

        [TestMethod]
        public async Task EncryptionStreamIteratorValidation()
        {
            await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);
            await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);

            // test GetItemLinqQueryable with ToEncryptionStreamIterator extension
            await EncryptionTests.ValidateQueryResponseAsync(EncryptionTests.encryptionContainer);
        }

        [TestMethod]
        public async Task EncryptionRudItem()
        {
            TestDoc testDoc = await EncryptionTests.UpsertItemAsync(
                EncryptionTests.encryptionContainer,
                TestDoc.Create(),
                EncryptionTests.dekId,
                TestDoc.PathsToEncrypt,
                HttpStatusCode.Created);

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.encryptionContainer, testDoc);

            testDoc.NonSensitive = Guid.NewGuid().ToString();
            testDoc.Sensitive = Guid.NewGuid().ToString();

            ItemResponse<TestDoc> upsertResponse = await EncryptionTests.UpsertItemAsync(
                EncryptionTests.encryptionContainer,
                testDoc,
                EncryptionTests.dekId,
                TestDoc.PathsToEncrypt,
                HttpStatusCode.OK);
            TestDoc updatedDoc = upsertResponse.Resource;

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.encryptionContainer, updatedDoc);

            updatedDoc.NonSensitive = Guid.NewGuid().ToString();
            updatedDoc.Sensitive = Guid.NewGuid().ToString();

            TestDoc replacedDoc = await EncryptionTests.ReplaceItemAsync(
                EncryptionTests.encryptionContainer,
                updatedDoc,
                EncryptionTests.dekId,
                TestDoc.PathsToEncrypt,
                upsertResponse.ETag);

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.encryptionContainer, replacedDoc);

            await EncryptionTests.DeleteItemAsync(EncryptionTests.encryptionContainer, replacedDoc);
        }

        [TestMethod]
        public async Task EncryptionResourceTokenAuthRestricted()
        {
            TestDoc testDoc = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);

            User restrictedUser = EncryptionTests.database.GetUser(Guid.NewGuid().ToString());
            await EncryptionTests.database.CreateUserAsync(restrictedUser.Id);

            PermissionProperties restrictedUserPermission = await restrictedUser.CreatePermissionAsync(
                new PermissionProperties(Guid.NewGuid().ToString(), PermissionMode.All, EncryptionTests.itemContainer));

            CosmosDataEncryptionKeyProvider dekProvider = new CosmosDataEncryptionKeyProvider(new TestKeyWrapProvider());
            TestEncryptor encryptor = new TestEncryptor(dekProvider);

            CosmosClient clientForRestrictedUser = TestCommon.CreateCosmosClient(
                restrictedUserPermission.Token);

            int failureCount = 0;
            Database databaseForRestrictedUser = clientForRestrictedUser.GetDatabase(EncryptionTests.database.Id);
            Container containerForRestrictedUser = databaseForRestrictedUser.GetContainer(EncryptionTests.itemContainer.Id);
            Action<DecryptionResult> errorHandler = (decryptionErrorDetails) =>
            {
                Assert.AreEqual(decryptionErrorDetails.Exception.Message, $"The CosmosDataEncryptionKeyProvider was not initialized.");
                failureCount++;
            };
            Container encryptionContainerForRestrictedUser = containerForRestrictedUser.WithEncryptor(encryptor);

            await EncryptionTests.PerformForbiddenOperationAsync(() =>
                dekProvider.InitializeAsync(databaseForRestrictedUser, EncryptionTests.keyContainer.Id), "CosmosDekProvider.InitializeAsync");

            await EncryptionTests.PerformOperationOnUninitializedDekProviderAsync(() =>
                dekProvider.DataEncryptionKeyContainer.ReadDataEncryptionKeyAsync(EncryptionTests.dekId), "DEK.ReadAsync");

            await encryptionContainerForRestrictedUser.ReadItemAsync<TestDoc>(
                testDoc.Id,
                new PartitionKey(testDoc.PK),
                new EncryptionItemRequestOptions
                {
                    DecryptionResultHandler = errorHandler
                });
            Assert.AreEqual(failureCount, 1);

            await encryptionContainerForRestrictedUser.ReadItemStreamAsync(
                testDoc.Id,
                new PartitionKey(testDoc.PK),
                new EncryptionItemRequestOptions
                {
                    DecryptionResultHandler = errorHandler
                });
            Assert.AreEqual(failureCount, 2);
        }

        [TestMethod]
        public async Task EncryptionResourceTokenAuthAllowed()
        {
            User keyManagerUser = EncryptionTests.database.GetUser(Guid.NewGuid().ToString());
            await EncryptionTests.database.CreateUserAsync(keyManagerUser.Id);

            PermissionProperties keyManagerUserPermission = await keyManagerUser.CreatePermissionAsync(
                new PermissionProperties(Guid.NewGuid().ToString(), PermissionMode.All, EncryptionTests.keyContainer));

            CosmosDataEncryptionKeyProvider dekProvider = new CosmosDataEncryptionKeyProvider(new TestKeyWrapProvider());
            TestEncryptor encryptor = new TestEncryptor(dekProvider);
            CosmosClient clientForKeyManagerUser = TestCommon.CreateCosmosClient(keyManagerUserPermission.Token);

            Database databaseForKeyManagerUser = clientForKeyManagerUser.GetDatabase(EncryptionTests.database.Id);

            await dekProvider.InitializeAsync(databaseForKeyManagerUser, EncryptionTests.keyContainer.Id);

            DataEncryptionKeyProperties readDekProperties = await dekProvider.DataEncryptionKeyContainer.ReadDataEncryptionKeyAsync(EncryptionTests.dekId);
            Assert.AreEqual(EncryptionTests.dekProperties, readDekProperties);
        }

        [TestMethod]
        public async Task EncryptionRestrictedProperties()
        {
            TestDoc testDoc = TestDoc.Create();

            try
            {
                await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, new List<string>() { "/id" });
                Assert.Fail("Expected item creation with id specified to be encrypted to fail.");
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
            }

            try
            {
                await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, new List<string>() { "/PK" });
                Assert.Fail("Expected item creation with PK specified to be encrypted to fail.");
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
            }
        }

        [TestMethod]
        public async Task EncryptionBulkCrud()
        {
            TestDoc docToReplace = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);
            docToReplace.NonSensitive = Guid.NewGuid().ToString();
            docToReplace.Sensitive = Guid.NewGuid().ToString();

            TestDoc docToUpsert = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);
            docToUpsert.NonSensitive = Guid.NewGuid().ToString();
            docToUpsert.Sensitive = Guid.NewGuid().ToString();

            TestDoc docToDelete = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, EncryptionTests.dekId, TestDoc.PathsToEncrypt);

            CosmosClient clientWithBulk = TestCommon.CreateCosmosClient(builder => builder
                .WithBulkExecution(true)
                .Build());

            Database databaseWithBulk = clientWithBulk.GetDatabase(EncryptionTests.database.Id);
            Container containerWithBulk = databaseWithBulk.GetContainer(EncryptionTests.itemContainer.Id);
            Container encryptionContainerWithBulk = containerWithBulk.WithEncryptor(EncryptionTests.encryptor);

            List<Task> tasks = new List<Task>()
            {
                EncryptionTests.CreateItemAsync(encryptionContainerWithBulk, EncryptionTests.dekId, TestDoc.PathsToEncrypt),
                EncryptionTests.UpsertItemAsync(encryptionContainerWithBulk, TestDoc.Create(), EncryptionTests.dekId, TestDoc.PathsToEncrypt, HttpStatusCode.Created),
                EncryptionTests.ReplaceItemAsync(encryptionContainerWithBulk, docToReplace, EncryptionTests.dekId, TestDoc.PathsToEncrypt),
                EncryptionTests.UpsertItemAsync(encryptionContainerWithBulk, docToUpsert, EncryptionTests.dekId, TestDoc.PathsToEncrypt, HttpStatusCode.OK),
                EncryptionTests.DeleteItemAsync(encryptionContainerWithBulk, docToDelete)
            };

            await Task.WhenAll(tasks);
        }

        [TestMethod]
        public async Task EncryptionTransactionBatchCrud()
        {
            string partitionKey = "thePK";
            string dek1 = EncryptionTests.dekId;
            string dek2 = "dek2Forbatch";
            await EncryptionTests.CreateDekAsync(EncryptionTests.dekProvider, dek2);

            TestDoc doc1ToCreate = TestDoc.Create(partitionKey);
            TestDoc doc2ToCreate = TestDoc.Create(partitionKey);
            TestDoc doc3ToCreate = TestDoc.Create(partitionKey);
            TestDoc doc4ToCreate = TestDoc.Create(partitionKey);

            ItemResponse<TestDoc> doc1ToReplaceCreateResponse = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, dek1, TestDoc.PathsToEncrypt, partitionKey);
            TestDoc doc1ToReplace = doc1ToReplaceCreateResponse.Resource;
            doc1ToReplace.NonSensitive = Guid.NewGuid().ToString();
            doc1ToReplace.Sensitive = Guid.NewGuid().ToString();

            TestDoc doc2ToReplace = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, dek2, TestDoc.PathsToEncrypt, partitionKey);
            doc2ToReplace.NonSensitive = Guid.NewGuid().ToString();
            doc2ToReplace.Sensitive = Guid.NewGuid().ToString();

            TestDoc doc1ToUpsert = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, dek2, TestDoc.PathsToEncrypt, partitionKey);
            doc1ToUpsert.NonSensitive = Guid.NewGuid().ToString();
            doc1ToUpsert.Sensitive = Guid.NewGuid().ToString();

            TestDoc doc2ToUpsert = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, dek1, TestDoc.PathsToEncrypt, partitionKey);
            doc2ToUpsert.NonSensitive = Guid.NewGuid().ToString();
            doc2ToUpsert.Sensitive = Guid.NewGuid().ToString();

            TestDoc docToDelete = await EncryptionTests.CreateItemAsync(EncryptionTests.encryptionContainer, dek1, TestDoc.PathsToEncrypt, partitionKey);

            TransactionalBatchResponse batchResponse = await EncryptionTests.encryptionContainer.CreateTransactionalBatch(new Cosmos.PartitionKey(partitionKey))
                .CreateItem(doc1ToCreate, EncryptionTests.GetBatchItemRequestOptions(dek1, TestDoc.PathsToEncrypt))
                .CreateItemStream(doc2ToCreate.ToStream(), EncryptionTests.GetBatchItemRequestOptions(dek2, TestDoc.PathsToEncrypt))
                .ReplaceItem(doc1ToReplace.Id, doc1ToReplace, EncryptionTests.GetBatchItemRequestOptions(dek2, TestDoc.PathsToEncrypt, doc1ToReplaceCreateResponse.ETag))
                .CreateItem(doc3ToCreate)
                .CreateItem(doc4ToCreate, EncryptionTests.GetBatchItemRequestOptions(dek1, new List<string>())) // empty PathsToEncrypt list
                .ReplaceItemStream(doc2ToReplace.Id, doc2ToReplace.ToStream(), EncryptionTests.GetBatchItemRequestOptions(dek2, TestDoc.PathsToEncrypt))
                .UpsertItem(doc1ToUpsert, EncryptionTests.GetBatchItemRequestOptions(dek1, TestDoc.PathsToEncrypt))
                .DeleteItem(docToDelete.Id)
                .UpsertItemStream(doc2ToUpsert.ToStream(), EncryptionTests.GetBatchItemRequestOptions(dek2, TestDoc.PathsToEncrypt))
                .ExecuteAsync();

            Assert.AreEqual(HttpStatusCode.OK, batchResponse.StatusCode);

            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.encryptionContainer, doc1ToCreate);
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.encryptionContainer, doc2ToCreate);
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.encryptionContainer, doc3ToCreate);
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.encryptionContainer, doc4ToCreate);
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.encryptionContainer, doc1ToReplace);
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.encryptionContainer, doc2ToReplace);
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.encryptionContainer, doc1ToUpsert);
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.encryptionContainer, doc2ToUpsert);

            ResponseMessage readResponseMessage = await EncryptionTests.encryptionContainer.ReadItemStreamAsync(docToDelete.Id, new PartitionKey(docToDelete.PK));
            Assert.AreEqual(HttpStatusCode.NotFound, readResponseMessage.StatusCode);

            // Validate that the documents are encrypted as expected by trying to retrieve through regular (non-encryption) container
            doc1ToCreate.Sensitive = null;
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.itemContainer, doc1ToCreate);

            doc2ToCreate.Sensitive = null;
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.itemContainer, doc2ToCreate);

            // doc3ToCreate, doc4ToCreate wasn't encrypted
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.itemContainer, doc3ToCreate);
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.itemContainer, doc4ToCreate);

            doc1ToReplace.Sensitive = null;
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.itemContainer, doc1ToReplace);

            doc2ToReplace.Sensitive = null;
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.itemContainer, doc2ToReplace);

            doc1ToUpsert.Sensitive = null;
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.itemContainer, doc1ToUpsert);

            doc2ToUpsert.Sensitive = null;
            await EncryptionTests.VerifyItemByReadAsync(EncryptionTests.itemContainer, doc2ToUpsert);
        }

        private static async Task ValidateSprocResultsAsync(Container container, TestDoc expectedDoc)
        {
            string sprocId = Guid.NewGuid().ToString();
            string sprocBody = @"function(docId) {
                var context = getContext();
                var collection = context.getCollection();
                var docUri =  collection.getAltLink() + '/docs/' + docId;
                var response = context.getResponse();

                collection.readDocument(docUri, { },
                    function(error, resource, options) {
                        response.setBody(resource);
                    });
            }";

            StoredProcedureResponse storedProcedureResponse =
                await container.Scripts.CreateStoredProcedureAsync(new StoredProcedureProperties(sprocId, sprocBody));
            Assert.AreEqual(HttpStatusCode.Created, storedProcedureResponse.StatusCode);

            StoredProcedureExecuteResponse<TestDoc> sprocResponse = await container.Scripts.ExecuteStoredProcedureAsync<TestDoc>(
                sprocId,
                new PartitionKey(expectedDoc.PK),
                parameters: new dynamic[] { expectedDoc.Id });

            Assert.AreEqual(expectedDoc, sprocResponse.Resource);
        }

        // One of query or queryDefinition is to be passed in non-null
        private static async Task ValidateQueryResultsAsync(
            Container container,
            string query = null,
            TestDoc expectedDoc = null,
            QueryDefinition queryDefinition = null)
        {
            QueryRequestOptions requestOptions = expectedDoc != null
                ? new QueryRequestOptions()
                {
                    PartitionKey = new PartitionKey(expectedDoc.PK),
                }
                : null;

            FeedIterator<TestDoc> queryResponseIterator;
            if (query != null)
            {
                queryResponseIterator = container.GetItemQueryIterator<TestDoc>(query, requestOptions: requestOptions);
            }
            else
            {
                queryResponseIterator = container.GetItemQueryIterator<TestDoc>(queryDefinition, requestOptions: requestOptions);
            }

            FeedResponse<TestDoc> readDocs = await queryResponseIterator.ReadNextAsync();
            Assert.AreEqual(null, readDocs.ContinuationToken);

            if (expectedDoc != null)
            {
                Assert.AreEqual(1, readDocs.Count);
                TestDoc readDoc = readDocs.Single();
                Assert.AreEqual(expectedDoc, readDoc);
            }
            else
            {
                Assert.AreEqual(0, readDocs.Count);
            }
        }

        private static async Task ValidateQueryResultsMultipleDocumentsAsync(
            Container container,
            TestDoc testDoc1,
            TestDoc testDoc2,
            string query,
            QueryRequestOptions requestOptions = null)
        {
            FeedIterator<TestDoc> queryResponseIterator;

            if (query == null)
            {
                IOrderedQueryable<TestDoc> linqQueryable = container.GetItemLinqQueryable<TestDoc>();
                queryResponseIterator = container.ToEncryptionFeedIterator<TestDoc>(linqQueryable, requestOptions);
            }
            else
            {
                queryResponseIterator = container.GetItemQueryIterator<TestDoc>(query, requestOptions: requestOptions);
            }

            FeedResponse<TestDoc> readDocs = await queryResponseIterator.ReadNextAsync();
            Assert.AreEqual(null, readDocs.ContinuationToken);

            if (query == null)
            {
                Assert.IsTrue(readDocs.Count >= 2);
            }
            else
            {
                Assert.AreEqual(2, readDocs.Count);
            }

            for (int index = 0; index < readDocs.Count; index++)
            {
                if (readDocs.ElementAt(index).Id.Equals(testDoc1.Id))
                {
                    Assert.AreEqual(readDocs.ElementAt(index), testDoc1);
                }
                else if (readDocs.ElementAt(index).Id.Equals(testDoc2.Id))
                {
                    Assert.AreEqual(readDocs.ElementAt(index), testDoc2);
                }
            }
        }

        private static async Task ValidatePropertyEncryptedQueryResponseAsync(Container container,
           string query = null)
        {
            FeedIterator feedIterator;
            if (query == null)
            {
                IOrderedQueryable<TestDoc> linqQueryable = container.GetItemLinqQueryable<TestDoc>();
                feedIterator = container.ToEncryptionStreamIterator(linqQueryable);
            }
            else
            {
                feedIterator = container.GetItemQueryStreamIterator(query);
            }

            ResponseMessage response = await feedIterator.ReadNextAsync();
            Assert.IsTrue(response.IsSuccessStatusCode);
            Assert.IsNull(response.ErrorMessage);
        }
        private static async Task ValidateQueryResponseAsync(Container container,
            string query = null)
        {
            FeedIterator feedIterator;
            if (query == null)
            {
                IOrderedQueryable<TestDoc> linqQueryable = container.GetItemLinqQueryable<TestDoc>();
                feedIterator = container.ToEncryptionStreamIterator(linqQueryable);
            }
            else
            {
                feedIterator = container.GetItemQueryStreamIterator(query);
            }

            while (feedIterator.HasMoreResults)
            {
                ResponseMessage response = await feedIterator.ReadNextAsync();
                Assert.IsTrue(response.IsSuccessStatusCode);
                Assert.IsNull(response.ErrorMessage);
            }
        }

        private async Task ValidateChangeFeedIteratorResponse(
            Container container,
            TestDoc testDoc1,
            TestDoc testDoc2,
            Action<DecryptionResult> DecryptionResultHandler = null)
        {
            //ChangeFeedRequestOption.StartTime  StartTime = ChangeFeedRequestOptions.StartFrom.CreateFromTime(DateTime.MinValue.ToUniversalTime());            
            FeedIterator<TestDoc> changeIterator = container.GetChangeFeedIterator<TestDoc>(
                continuationToken: null,
                new EncryptionChangeFeedRequestOptions()
                {
                    StartTime = DateTime.MinValue.ToUniversalTime(),
                    DecryptionResultHandler = DecryptionResultHandler
                });

            List<TestDoc> changeFeedReturnedDocs = new List<TestDoc>();
            while (changeIterator.HasMoreResults)
            {
                FeedResponse<TestDoc> testDocs = await changeIterator.ReadNextAsync();
                for (int index = 0; index < testDocs.Count; index++)
                {
                    if (testDocs.Resource.ElementAt(index).Id.Equals(testDoc1.Id) || testDocs.Resource.ElementAt(index).Id.Equals(testDoc2.Id))
                    {
                        changeFeedReturnedDocs.Add(testDocs.Resource.ElementAt(index));
                    }
                }
            }

            Assert.AreEqual(changeFeedReturnedDocs.Count, 2);
            Assert.AreEqual(testDoc1, changeFeedReturnedDocs[changeFeedReturnedDocs.Count - 2]);
            Assert.AreEqual(testDoc2, changeFeedReturnedDocs[changeFeedReturnedDocs.Count - 1]);
        }
        
        private async Task ValidateChangeFeedProcessorResponse(
            Container container,
            TestDoc testDoc1,
            TestDoc testDoc2)
        {
            List<TestDoc> changeFeedReturnedDocs = new List<TestDoc>();
            ChangeFeedProcessor cfp = container.GetChangeFeedProcessorBuilder(
                "testCFP",
                (IReadOnlyCollection<TestDoc> changes, CancellationToken cancellationToken)
                =>
                {
                    changeFeedReturnedDocs.AddRange(changes);
                    return Task.CompletedTask;
                })
                //.WithInMemoryLeaseContainer()
                //.WithStartFromBeginning()
                .Build();

            await cfp.StartAsync();
            await Task.Delay(2000);
            await cfp.StopAsync();

            Assert.IsTrue(changeFeedReturnedDocs.Count >= 2);

            foreach (TestDoc testDoc in changeFeedReturnedDocs)
            {
                if (testDoc.Id.Equals(testDoc1.Id))
                {
                    Assert.AreEqual(testDoc1, testDoc);
                }
                else if (testDoc.Id.Equals(testDoc2.Id))
                {
                    Assert.AreEqual(testDoc2, testDoc);
                }
            }
        }

        private static void ErrorHandler(DecryptionResult decryptionErrorDetails)
        {
            Assert.AreEqual(decryptionErrorDetails.Exception.Message, "Null DataEncryptionKey returned.");

            using (MemoryStream memoryStream = new MemoryStream(decryptionErrorDetails.EncryptedStream.ToArray()))
            {
                JObject itemJObj = TestCommon.FromStream<JObject>(memoryStream);
                JProperty encryptionPropertiesJProp = itemJObj.Property("_ei");
                Assert.IsNotNull(encryptionPropertiesJProp);
                Assert.AreEqual(itemJObj.Property("id").Value.ToString(), EncryptionTests.decryptionFailedDocId);
            }                
        }

        private static ItemRequestOptions GetItemRequestOptionsWithDecryptionResultHandler()
        {
            return new EncryptionItemRequestOptions
            {
                DecryptionResultHandler = EncryptionTests.ErrorHandler
            };
        }

        private static CosmosClient GetClient()
        {
            return TestCommon.CreateCosmosClient();
        }

        private static async Task IterateDekFeedAsync(
            CosmosDataEncryptionKeyProvider dekProvider,
            List<string> expectedDekIds,
            bool isExpectedDeksCompleteSetForRequest,
            bool isResultOrderExpected,
            string query,
            int? itemCountInPage = null)
        {
            int remainingItemCount = expectedDekIds.Count;
            QueryRequestOptions requestOptions = null;
            if (itemCountInPage.HasValue)
            {
                requestOptions = new QueryRequestOptions()
                {
                    MaxItemCount = itemCountInPage
                };
            }

            FeedIterator<DataEncryptionKeyProperties> dekIterator = dekProvider.DataEncryptionKeyContainer
                .GetDataEncryptionKeyQueryIterator<DataEncryptionKeyProperties>(
                    query,
                    requestOptions: requestOptions);

            Assert.IsTrue(dekIterator.HasMoreResults);

            List<string> readDekIds = new List<string>();
            while (remainingItemCount > 0)
            {
                FeedResponse<DataEncryptionKeyProperties> page = await dekIterator.ReadNextAsync();
                if (itemCountInPage.HasValue)
                {
                    // last page
                    if (remainingItemCount < itemCountInPage.Value)
                    {
                        Assert.AreEqual(remainingItemCount, page.Count);
                    }
                    else
                    {
                        Assert.AreEqual(itemCountInPage.Value, page.Count);
                    }
                }
                else
                {
                    Assert.AreEqual(expectedDekIds.Count, page.Count);
                }

                remainingItemCount -= page.Count;
                if (isExpectedDeksCompleteSetForRequest)
                {
                    Assert.AreEqual(remainingItemCount > 0, dekIterator.HasMoreResults);
                }

                foreach (DataEncryptionKeyProperties dek in page.Resource)
                {
                    readDekIds.Add(dek.Id);
                }
            }

            if (isResultOrderExpected)
            {
                Assert.IsTrue(expectedDekIds.SequenceEqual(readDekIds));
            }
            else
            {
                Assert.IsTrue(expectedDekIds.ToHashSet().SetEquals(readDekIds));
            }
        }

        private static async Task<ItemResponse<TestDoc>> UpsertItemAsync(
            Container container,
            TestDoc testDoc,
            string dekId,
            List<string> pathsToEncrypt,
            HttpStatusCode expectedStatusCode)
        {
            ItemResponse<TestDoc> upsertResponse = await container.UpsertItemAsync(
                testDoc,
                new PartitionKey(testDoc.PK),
                EncryptionTests.GetRequestOptions(dekId, pathsToEncrypt));
            Assert.AreEqual(expectedStatusCode, upsertResponse.StatusCode);
            Assert.AreEqual(testDoc, upsertResponse.Resource);
            return upsertResponse;
        }

        private static async Task<ItemResponse<TestDoc>> CreateItemAsync(
            Container container,
            string dekId,
            List<string> pathsToEncrypt,
            string partitionKey = null)
        {
            TestDoc testDoc = TestDoc.Create(partitionKey);
            ItemResponse<TestDoc> createResponse = await container.CreateItemAsync(
                testDoc,
                new PartitionKey(testDoc.PK),
                EncryptionTests.GetRequestOptions(dekId, pathsToEncrypt));
            Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
            Assert.AreEqual(testDoc, createResponse.Resource);
            return createResponse;
        }

        private static async Task<ItemResponse<TestDoc>> ReplaceItemAsync(
            Container encryptedContainer,
            TestDoc testDoc,
            string dekId,
            List<string> pathsToEncrypt,
            string etag = null)
        {
            ItemResponse<TestDoc> replaceResponse = await encryptedContainer.ReplaceItemAsync(
                testDoc,
                testDoc.Id,
                new PartitionKey(testDoc.PK),
                EncryptionTests.GetRequestOptions(dekId, pathsToEncrypt, etag));

            Assert.AreEqual(HttpStatusCode.OK, replaceResponse.StatusCode);
            Assert.AreEqual(testDoc, replaceResponse.Resource);
            return replaceResponse;
        }

        private static async Task<ItemResponse<TestDoc>> DeleteItemAsync(
            Container encryptedContainer,
            TestDoc testDoc)
        {
            ItemResponse<TestDoc> deleteResponse = await encryptedContainer.DeleteItemAsync<TestDoc>(
                testDoc.Id,
                new PartitionKey(testDoc.PK));

            Assert.AreEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            Assert.IsNull(deleteResponse.Resource);
            return deleteResponse;
        }

        private static EncryptionItemRequestOptions GetRequestOptions(
            string dekId,
            List<string> pathsToEncrypt,
            string ifMatchEtag = null)
        {
            return new EncryptionItemRequestOptions
            {
                EncryptionOptions = EncryptionTests.GetEncryptionOptions(dekId, pathsToEncrypt),
                IfMatchEtag = ifMatchEtag
            };
        }

        private static TransactionalBatchItemRequestOptions GetBatchItemRequestOptions(
            string dekId,
            List<string> pathsToEncrypt,
            string ifMatchEtag = null)
        {
            return new EncryptionTransactionalBatchItemRequestOptions
            {
                EncryptionOptions = EncryptionTests.GetEncryptionOptions(dekId, pathsToEncrypt),
                IfMatchEtag = ifMatchEtag
            };
        }

        private static EncryptionOptions GetEncryptionOptions(
            string dekId,
            List<string> pathsToEncrypt)
        {
            return new EncryptionOptions()
            {
                DataEncryptionKeyId = dekId,
                EncryptionAlgorithm = CosmosEncryptionAlgorithm.AEAes256CbcHmacSha256Randomized,
                PathsToEncrypt = pathsToEncrypt
            };
        }

        private static async Task VerifyItemByReadStreamAsync(Container container, TestDoc testDoc, ItemRequestOptions requestOptions = null)
        {
            ResponseMessage readResponseMessage = await container.ReadItemStreamAsync(testDoc.Id, new PartitionKey(testDoc.PK), requestOptions);
            Assert.AreEqual(HttpStatusCode.OK, readResponseMessage.StatusCode);
            Assert.IsNotNull(readResponseMessage.Content);
            TestDoc readDoc = TestCommon.FromStream<TestDoc>(readResponseMessage.Content);
            Assert.AreEqual(testDoc, readDoc);
        }

        private static async Task VerifyItemByReadAsync(Container container, TestDoc testDoc, ItemRequestOptions requestOptions = null)
        {
            ItemResponse<TestDoc> readResponse = await container.ReadItemAsync<TestDoc>(testDoc.Id, new PartitionKey(testDoc.PK), requestOptions);
            Assert.AreEqual(HttpStatusCode.OK, readResponse.StatusCode);
            Assert.AreEqual(testDoc, readResponse.Resource);
        }

        private static async Task<DataEncryptionKeyProperties> CreateDekAsync(CosmosDataEncryptionKeyProvider dekProvider, string dekId)
        {
            ItemResponse<DataEncryptionKeyProperties> dekResponse = await dekProvider.DataEncryptionKeyContainer.CreateDataEncryptionKeyAsync(
                dekId,
                CosmosEncryptionAlgorithm.AEAes256CbcHmacSha256Randomized,
                EncryptionTests.metadata1);

            Assert.AreEqual(HttpStatusCode.Created, dekResponse.StatusCode);
            
            return VerifyDekResponse(dekResponse,
                dekId);
        }

        private static async Task<DataEncryptionKeyProperties> CreatePropertyDekAsync(CosmosDataEncryptionKeyProvider dekProvider, string dekId)
        {
            ItemResponse<DataEncryptionKeyProperties> dekResponse = await dekProvider.DataEncryptionKeyContainer.CreateDataEncryptionKeyAsync(
                dekId,
                CosmosEncryptionAlgorithm.AEAD_AES_256_CBC_HMAC_SHA256,
                EncryptionTests.metadata1);

            Assert.AreEqual(HttpStatusCode.Created, dekResponse.StatusCode);

            return VerifyDekResponse(dekResponse,
                dekId);
        }

        private static DataEncryptionKeyProperties VerifyDekResponse(
            ItemResponse<DataEncryptionKeyProperties> dekResponse,
            string dekId)
        {
            Assert.IsTrue(dekResponse.RequestCharge > 0);
            Assert.IsNotNull(dekResponse.ETag);

            DataEncryptionKeyProperties dekProperties = dekResponse.Resource;
            Assert.IsNotNull(dekProperties);
            Assert.AreEqual(dekResponse.ETag, dekProperties.ETag);
            Assert.AreEqual(dekId, dekProperties.Id);
            Assert.IsNotNull(dekProperties.SelfLink);
            Assert.IsNotNull(dekProperties.CreatedTime);
            Assert.IsNotNull(dekProperties.LastModified);
            
            return dekProperties;
        }

        private static async Task PerformForbiddenOperationAsync(Func<Task> func, string operationName)
        {
            try
            {
                await func();
                Assert.Fail($"Expected resource token based client to not be able to perform {operationName}");
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
            }
        }

        private static async Task PerformOperationOnUninitializedDekProviderAsync(Func<Task> func, string operationName)
        {
            try
            {
                await func();
                Assert.Fail($"Expected {operationName} to not work on uninitialized CosmosDataEncryptionKeyProvider.");
            }
            catch (InvalidOperationException ex)
            {
                Assert.IsTrue(ex.Message.Contains("The CosmosDataEncryptionKeyProvider was not initialized."));
            }
        }

        public class TestDoc
        {
            public static List<string> PathsToEncrypt { get; } = new List<string>() { "/Sensitive" };
            public static List<string> PropertyPathsToEncrypt { get; } = new List<string> { "/Name" };
            public static List<string> PropertyPathsToEncrypt2 { get; } = new List<string> { "/City" };
            public static List<string> Property2PathsToEncrypt { get; } = new List<string> { "/SSN", "/Name" };

            [JsonProperty("id")]
            public string Id { get; set; }
            public string PK { get; set; }
            public string Name { get; set; }
            public string City { get; set; }
            public string SSN { get; set; }
            public string NonSensitive { get; set; }
            public string Sensitive { get; set; }

            public TestDoc()
            {
            }

            public TestDoc(TestDoc other)
            {
                this.Id = other.Id;
                this.PK = other.PK;
                this.Name = other.Name;
                this.City = other.City;
                this.SSN = other.SSN;
                this.NonSensitive = other.NonSensitive;
                this.Sensitive = other.Sensitive;
            }

            public override bool Equals(object obj)
            {
                return obj is TestDoc doc
                       && this.Id == doc.Id
                       && this.PK == doc.PK
                       && this.Name == doc.Name
                       && this.City == doc.City
                       && this.SSN == doc.SSN
                       && this.NonSensitive == doc.NonSensitive
                       && this.Sensitive == doc.Sensitive;
            }

            public override int GetHashCode()
            {
                int hashCode = 1652434776;
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.Id);
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.PK);
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.Name);
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.City);
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.NonSensitive);
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.SSN);
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.Sensitive);
                return hashCode;
            }

            public static TestDoc Create(string partitionKey = null)
            {
                return new TestDoc()
                {
                    Id = Guid.NewGuid().ToString(),
                    PK = partitionKey ?? Guid.NewGuid().ToString(),
                    Name = "myName",
                    City = "myCity",
                    SSN = "123",
                    NonSensitive = Guid.NewGuid().ToString(),
                    Sensitive = Guid.NewGuid().ToString(),
                };
            }

            public Stream ToStream()
            {
                return TestCommon.ToStream(this);
            }
        }

        private class TestKeyWrapProvider : EncryptionKeyWrapProvider
        {
            public override Task<EncryptionKeyUnwrapResult> UnwrapKeyAsync(byte[] wrappedKey, EncryptionKeyWrapMetadata metadata, CancellationToken cancellationToken)
            {
                int moveBy = metadata.Value == EncryptionTests.metadata1.Value + EncryptionTests.metadataUpdateSuffix ? 1 : 2;
                return Task.FromResult(new EncryptionKeyUnwrapResult(wrappedKey.Select(b => (byte)(b - moveBy)).ToArray(), EncryptionTests.cacheTTL));
            }

            public override Task<EncryptionKeyWrapResult> WrapKeyAsync(byte[] key, EncryptionKeyWrapMetadata metadata, CancellationToken cancellationToken)
            {
                EncryptionKeyWrapMetadata responseMetadata = new EncryptionKeyWrapMetadata(metadata.Value + EncryptionTests.metadataUpdateSuffix);
                int moveBy = metadata.Value == EncryptionTests.metadata1.Value ? 1 : 2;
                return Task.FromResult(new EncryptionKeyWrapResult(key.Select(b => (byte)(b + moveBy)).ToArray(), responseMetadata));
            }
        }

        // This class is same as CosmosEncryptor but copied so as to induce decryption failure easily for testing.
        private class TestEncryptor : Encryptor
        {
            public DataEncryptionKeyProvider DataEncryptionKeyProvider { get; }
            public bool FailDecryption { get; set; }

            public TestEncryptor(DataEncryptionKeyProvider dataEncryptionKeyProvider)
            {
                this.DataEncryptionKeyProvider = dataEncryptionKeyProvider;
                this.FailDecryption = false;
            }

            public override async Task<byte[]> DecryptAsync(
                byte[] cipherText,
                string dataEncryptionKeyId,
                string encryptionAlgorithm,
                CancellationToken cancellationToken = default)
            {
                if (this.FailDecryption && dataEncryptionKeyId.Equals("failDek"))
                {
                    throw new InvalidOperationException($"Null {nameof(DataEncryptionKey)} returned.");
                }

                DataEncryptionKey dek = await this.DataEncryptionKeyProvider.FetchDataEncryptionKeyAsync(
                    dataEncryptionKeyId,
                    encryptionAlgorithm,
                    cancellationToken);

                if (dek == null)
                {
                    throw new InvalidOperationException($"Null {nameof(DataEncryptionKey)} returned from {nameof(this.DataEncryptionKeyProvider.FetchDataEncryptionKeyAsync)}.");
                }

                return dek.DecryptData(cipherText);
            }

            public override async Task<byte[]> EncryptAsync(
                byte[] plainText,
                string dataEncryptionKeyId,
                string encryptionAlgorithm,
                CancellationToken cancellationToken = default)
            {
                DataEncryptionKey dek = await this.DataEncryptionKeyProvider.FetchDataEncryptionKeyAsync(
                    dataEncryptionKeyId,
                    encryptionAlgorithm,
                    cancellationToken);

                return dek.EncryptData(plainText);
            }
        }
    }
}

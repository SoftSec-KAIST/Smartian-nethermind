﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Evm;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BlockchainProcessorTests
    {
        [Test]
        public async Task Test()
        {
            string[] files = Directory.GetFiles(Path.Combine(TestContext.CurrentContext.TestDirectory, "TestRopstenBlocks"));
            Assert.Greater(files.Length, 4000);

            /* logging & instrumentation */
            var logger = NullLogger.Instance;

            /* spec */
            var sealEngine = NullSealEngine.Instance;
            var specProvider = RopstenSpecProvider.Instance;

            /* store & validation */
            var blockTree = new BlockTree(specProvider, logger);
            var difficultyCalculator = new DifficultyCalculator(specProvider);
            var headerValidator = new HeaderValidator(difficultyCalculator, blockTree, sealEngine, specProvider, logger);
            var ommersValidator = new OmmersValidator(blockTree, headerValidator, logger);
            var transactionValidator = new TransactionValidator(new SignatureValidator(ChainId.Ropsten));
            var blockValidator = new BlockValidator(transactionValidator, headerValidator, ommersValidator, specProvider, logger);

            /* state & storage */
            var codeDb = new InMemoryDb();
            var stateDb = new InMemoryDb();
            var stateTree = new StateTree(stateDb);
            var stateProvider = new StateProvider(stateTree, logger, codeDb);
            var storageDbProvider = new DbProvider(logger);
            var storageProvider = new StorageProvider(storageDbProvider, stateProvider, logger);

            /* blockchain processing */
            var ethereumSigner = new EthereumSigner(specProvider, logger);
            var transactionStore = new TransactionStore();
            var blockhashProvider = new BlockhashProvider(blockTree);
            var virtualMachine = new VirtualMachine(specProvider, stateProvider, storageProvider, blockhashProvider, logger);
            var processor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, ethereumSigner, logger);
            var rewardCalculator = new RewardCalculator(specProvider);
            var blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, processor, storageDbProvider, stateProvider, storageProvider, transactionStore, logger);
            var blockchainProcessor = new BlockchainProcessor(blockTree, sealEngine, transactionStore, difficultyCalculator, blockProcessor, logger);

            /* load ChainSpec and init */
            ChainSpecLoader loader = new ChainSpecLoader(new UnforgivingJsonSerializer());
            string path = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\Chains", "ropsten.json"));
            logger.Info($"Loading ChainSpec from {path}");
            ChainSpec chainSpec = loader.Load(File.ReadAllBytes(path));
            foreach (KeyValuePair<Address, BigInteger> allocation in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(allocation.Key, allocation.Value);
            }

            stateProvider.Commit(specProvider.GenesisSpec);
            chainSpec.Genesis.Header.StateRoot = stateProvider.StateRoot; // TODO: shall it be HeaderSpec and not BlockHeader?
            chainSpec.Genesis.Header.Hash = BlockHeader.CalculateHash(chainSpec.Genesis.Header);
            if (chainSpec.Genesis.Hash != new Keccak("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d"))
            {
                throw new Exception("Unexpected genesis hash");
            }

            /* start processing */
            blockchainProcessor.Start();
            blockTree.SuggestBlock(chainSpec.Genesis);

            List<Block> blocks = new List<Block>();
            foreach (string file in files)
            {
                try
                {
                    string rlpText = File.ReadAllText(file);
                    Rlp rlp = new Rlp(new Hex(rlpText));
                    Block block = Rlp.Decode<Block>(rlp);
                    blocks.Add(block);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    continue;
                }
            }

            blockchainProcessor.HeadBlockChanged += (sender, args) => Console.WriteLine(args.Block.Number);
            foreach (Block block in blocks.OrderBy(b => b.Number).Skip(1))
            {
                try
                {
                    blockTree.SuggestBlock(block);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"BLOCK {block.Number} failed, " + e);
                    throw;
                }
            }

            await blockchainProcessor.StopAsync(true).ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        throw t.Exception;
                    }

                    Console.WriteLine("COMPLETED");
                });
        }
    }
}
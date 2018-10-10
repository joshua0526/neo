using Akka.Actor;
using Akka.Configuration;
using Neo.Cryptography;
using Neo.IO;
using Neo.IO.Actors;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Plugins;
using Neo.SmartContract;
using Neo.Wallets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Neo.Consensus
{
    public sealed class SingleService : UntypedActor
    {
        public class Start { }
        internal class Timer { public uint Height; public byte ViewNumber; }

        private readonly ConsensusContext context = new ConsensusContext();
        private readonly NeoSystem system;
        private readonly Wallet wallet;
        private DateTime block_received_time;

        public SingleService(NeoSystem system, Wallet wallet)
        {
            this.system = system;
            this.wallet = wallet;
        }

        private void ChangeTimer(TimeSpan delay)
        {
            Context.System.Scheduler.ScheduleTellOnce(delay, Self, new Timer
            {
                Height = context.BlockIndex,
                ViewNumber = context.ViewNumber
            }, ActorRefs.NoSender);
        }

        private void FillContext()
        {
            IEnumerable<Transaction> mem_pool = Blockchain.Singleton.GetMemoryPool();
            foreach (IPolicyPlugin plugin in Plugin.Policies)
                mem_pool = plugin.FilterForBlock(mem_pool);
            List<Transaction> transactions = mem_pool.ToList();
            Fixed8 amount_netfee = Block.CalculateNetFee(transactions);
            TransactionOutput[] outputs = amount_netfee == Fixed8.Zero ? new TransactionOutput[0] : new[] { new TransactionOutput
            {
                AssetId = Blockchain.UtilityToken.Hash,
                Value = amount_netfee,
                ScriptHash = wallet.GetChangeAddress()
            } };
            while (true)
            {
                ulong nonce = GetNonce();
                MinerTransaction tx = new MinerTransaction
                {
                    Nonce = (uint)(nonce % (uint.MaxValue + 1ul)),
                    Attributes = new TransactionAttribute[0],
                    Inputs = new CoinReference[0],
                    Outputs = outputs,
                    Witnesses = new Witness[0]
                };
                if (!context.Snapshot.ContainsTransaction(tx.Hash))
                {
                    context.Nonce = nonce;
                    transactions.Insert(0, tx);
                    break;
                }
            }
            context.TransactionHashes = transactions.Select(p => p.Hash).ToArray();
            context.Transactions = transactions.ToDictionary(p => p.Hash);
            context.NextConsensus = Blockchain.GetConsensusAddress(context.Snapshot.GetValidators(transactions).ToArray());
        }

        private static ulong GetNonce()
        {
            byte[] nonce = new byte[sizeof(ulong)];
            Random rand = new Random();
            rand.NextBytes(nonce);
            return nonce.ToUInt64(0);
        }

        private void InitializeConsensus(byte view_number)
        {
            if (view_number == 0)
            {
                context.Reset(wallet);
            }
            TimeSpan span = DateTime.UtcNow - block_received_time;
            if (span >= Blockchain.TimePerBlock)
                ChangeTimer(TimeSpan.Zero);
            else
                ChangeTimer(Blockchain.TimePerBlock - span);

        }

        private void Log(string message, LogLevel level = LogLevel.Info)
        {
            Plugin.Log(nameof(SingleService), level, message);
        }

        private void OnPersistCompleted(Block block)
        {
            Log($"persist block: {block.Hash}");
            block_received_time = DateTime.UtcNow;
            InitializeConsensus(0);
        }

        protected override void OnReceive(object message)
        {
            switch (message)
            {
                case Start _:
                    OnStart();
                    break;
                case Timer timer:
                    OnTimer(timer);
                    break;
                case Blockchain.PersistCompleted completed:
                    OnPersistCompleted(completed.Block);
                    break;
            }
        }

        private void OnStart()
        {
            Log("OnStart");
            InitializeConsensus(0);
        }

        private void OnTimer(Timer timer)
        {
            if (timer.Height != context.BlockIndex || timer.ViewNumber != context.ViewNumber) return;
            Log($"timeout: height={timer.Height} view={timer.ViewNumber} state={context.State}");

            Log($"send prepare request: height={timer.Height} view={timer.ViewNumber}");
            {
                FillContext();
                context.Timestamp = Math.Max(DateTime.UtcNow.ToTimestamp(), context.Snapshot.GetHeader(context.PrevHash).Timestamp + 1);
                context.Signatures[context.MyIndex] = context.MakeHeader().Sign(context.KeyPair);
                if (context.TransactionHashes.All(p => context.Transactions.ContainsKey(p)))
                {
                    Contract contract = Contract.CreateSignatureContract(context.Validators[0]);
                    Block block = context.MakeHeader();
                    ContractParametersContext sc = new ContractParametersContext(block);
                    wallet.Sign(sc);
                    sc.Verifiable.Witnesses = sc.GetWitnesses();
                    block.Transactions = context.TransactionHashes.Select(p => context.Transactions[p]).ToArray();
                    Log($"relay block: {block.Hash}");
                    system.LocalNode.Tell(new LocalNode.Relay { Inventory = block });
                }
            }
            ChangeTimer(TimeSpan.FromSeconds(Blockchain.SecondsPerBlock << (timer.ViewNumber + 1)));
        }

        protected override void PostStop()
        {
            Log("OnStop");
            context.Dispose();
            base.PostStop();
        }

        public static Props Props(NeoSystem system, Wallet wallet)
        {
            return Akka.Actor.Props.Create(() => new SingleService(system, wallet)).WithMailbox("consensus-service-mailbox");
        }

    }
}

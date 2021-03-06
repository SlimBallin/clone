using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Bencodex;
using Bencodex.Types;
using Libplanet;
using Libplanet.Action;
using Libplanet.Assets;
using Libplanet.Blockchain;
using Libplanet.Blockchain.Renderers;
using Libplanet.Net;
using Libplanet.Headless.Hosting;
using Libplanet.Tx;
using MagicOnion;
using MagicOnion.Server;
using Nekoyume;
using Nekoyume.Action;
using Nekoyume.Shared.Services;
using Serilog;
using NineChroniclesActionType = Libplanet.Action.PolymorphicAction<Nekoyume.Action.ActionBase>;
using NodeExceptionType = Libplanet.Headless.NodeExceptionType;

namespace NineChronicles.Headless
{
    public class BlockChainService : ServiceBase<IBlockChainService>, IBlockChainService
    {
        private BlockChain<NineChroniclesActionType> _blockChain;
        private Swarm<NineChroniclesActionType> _swarm;
        private RpcContext _context;
        private Codec _codec;
        private LibplanetNodeServiceProperties<NineChroniclesActionType> _libplanetNodeServiceProperties;
        private DelayedRenderer<NineChroniclesActionType> _delayedRenderer;

        public BlockChainService(
            BlockChain<NineChroniclesActionType> blockChain,
            Swarm<NineChroniclesActionType> swarm,
            RpcContext context,
            LibplanetNodeServiceProperties<NineChroniclesActionType> libplanetNodeServiceProperties
        )
        {
            _blockChain = blockChain;
            _delayedRenderer = blockChain.Renderers
                .OfType<DelayedRenderer<NineChroniclesActionType>>()
                .FirstOrDefault();
            _swarm = swarm;
            _context = context;
            _codec = new Codec();
            _libplanetNodeServiceProperties = libplanetNodeServiceProperties;
        }

        public UnaryResult<bool> PutTransaction(byte[] txBytes)
        {
            try
            {
                Transaction<PolymorphicAction<ActionBase>> tx =
                    Transaction<PolymorphicAction<ActionBase>>.Deserialize(txBytes);

                try
                {
                    tx.Validate();
                    Log.Debug("PutTransaction: (nonce: {nonce}, id: {id})", tx.Nonce, tx.Id);
                    Log.Debug("StagedTransactions: {txIds}", string.Join(", ", _blockChain.GetStagedTransactionIds()));
                    _blockChain.StageTransaction(tx);
                    _swarm.BroadcastTxs(new[] {tx});

                    return UnaryResult(true);
                }
                catch (InvalidTxException ite)
                {
                    Log.Error(ite, $"{nameof(InvalidTxException)} occurred during {nameof(PutTransaction)}(). {{e}}", ite);
                    return UnaryResult(false);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unexpected exception occurred during {nameof(PutTransaction)}(). {{e}}", e);
                throw;
            }
        }

        public UnaryResult<byte[]> GetState(byte[] addressBytes)
        {
            var address = new Address(addressBytes);
            IValue state = _blockChain.GetState(address, _delayedRenderer?.Tip?.Hash);
            // FIXME: Null??? null ???????????? ???????????? ??? ???
            byte[] encoded = _codec.Encode(state ?? new Null());
            return UnaryResult(encoded);
        }

        public UnaryResult<byte[]> GetBalance(byte[] addressBytes, byte[] currencyBytes)
        {
            var address = new Address(addressBytes);
            var serializedCurrency = (Bencodex.Types.Dictionary)_codec.Decode(currencyBytes);
            Currency currency = CurrencyExtensions.Deserialize(serializedCurrency);
            FungibleAssetValue balance = _blockChain.GetBalance(address, currency);
            byte[] encoded = _codec.Encode(
              new Bencodex.Types.List(
                new IValue[] 
                {
                  balance.Currency.Serialize(),
                  (Integer) balance.RawValue,
                }
              )
            );
            return UnaryResult(encoded);
        }

        public UnaryResult<long> GetNextTxNonce(byte[] addressBytes)
        {
            var address = new Address(addressBytes);
            var nonce = _blockChain.GetNextTxNonce(address);
            Log.Debug("GetNextTxNonce: {nonce}", nonce);
            return UnaryResult(nonce);
        }

        public UnaryResult<bool> SetAddressesToSubscribe(IEnumerable<byte[]> addressesBytes)
        {
            _context.AddressesToSubscribe =
                addressesBytes.Select(ba => new Address(ba)).ToImmutableHashSet();
            Log.Debug(
                "Subscribed addresses: {addresses}",
                string.Join(", ", _context.AddressesToSubscribe));
            return UnaryResult(true);
        }

        public UnaryResult<bool> IsTransactionStaged(byte[] txidBytes)
        {
            var id = new TxId(txidBytes);
            var isStaged = _blockChain.GetStagedTransactionIds().Contains(id);
            Log.Debug(
                "Transaction {id} is {1}.",
                id,
                isStaged ? "staged" : "not staged");
            return UnaryResult(isStaged);
        }

        public UnaryResult<bool> ReportException(string code, string message)
        {
            Log.Debug(
                $"Reported exception from Unity player. " +
                $"(code: {code}, message: {message})"
            );

            switch (code)
            {
                case "26":
                case "27":
                    NodeExceptionType exceptionType = NodeExceptionType.ActionTimeout;
                    _libplanetNodeServiceProperties.NodeExceptionOccurred(exceptionType, message);
                    break;
            }

            return UnaryResult(true);
        }
    }
}

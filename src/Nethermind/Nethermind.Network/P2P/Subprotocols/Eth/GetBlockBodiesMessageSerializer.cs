﻿namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class GetBlockBodiesMessageSerializer : IMessageSerializer<GetBlockBodiesMessage>
    {
        public byte[] Serialize(GetBlockBodiesMessage message, IMessagePad pad = null)
        {
            throw new System.NotImplementedException();
        }

        public GetBlockBodiesMessage Deserialize(byte[] bytes)
        {
            throw new System.NotImplementedException();
        }
    }
}
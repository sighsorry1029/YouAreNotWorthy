using System;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

internal static class DirectPeerChecks
{
    internal static bool IsRemoteAdmin(ZRpc requester)
    {
        try
        {
            ZNet? net = ZNet.instance;
            ZNetPeer? peer = net?.GetConnectedPeers().Find(
                candidate => ReferenceEquals(candidate.m_rpc, requester));
            return (Object?)net != null
                   && net.IsServer()
                   && peer != null
                   && peer.IsReady()
                   && ReferenceEquals(peer.m_rpc, requester)
                   && net.IsAdmin(peer.m_socket.GetHostName());
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool IsServerConnection(ZRpc rpc)
    {
        try
        {
            ZNetPeer? peer = ZNet.instance?.GetServerPeer();
            return peer != null && peer.m_server && ReferenceEquals(peer.m_rpc, rpc);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

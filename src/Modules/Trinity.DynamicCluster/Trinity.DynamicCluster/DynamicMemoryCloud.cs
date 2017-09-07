﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Trinity;
using Trinity.Network.Messaging;
using Trinity.Diagnostics;
using System.Runtime.CompilerServices;
using Trinity.Network;
using Trinity.Utilities;
using Trinity.Storage;
using Trinity.DynamicCluster;

namespace Trinity.Storage
{
    public unsafe partial class DynamicMemoryCloud : MemoryCloud
    {
        //private int server_count = -1;
        private int partition_count = -1;
        private int my_partition_id = -1;
        private int my_proxy_id = -1;
        private ChunkCollection m_chunks = new ChunkCollection();
        //private int my_partition_id = -1;
        //Dictionary<int, int> server_in_partition = new Dictionary<int, int>();
        //private List<List<int>> server_host_chunk = new List<List<int>>();
        //private List<int> my_hosting_chunks = new List<int>();

        private static List<long> chunk_range = new List<long>();
        public static List<long> ChunkRange
        {
            get { return chunk_range; }

            //set { chunk_range = value; }
        }

        internal ClusterConfig cluster_config;
        internal INameService m_nameservice;
        internal NameDescriptor m_namedescriptor = new NameDescriptor();



        public void OnStorageJoin(RemoteStorage remoteStorage)
        {
            var remotestorage_info = _QueryChunkedRemoteStorageInformation(remoteStorage);
            var chunks = remotestorage_info.chunks;
            var p = remotestorage_info.partitionid;
        }

        private object _QueryChunkedRemoteStorageInformation(RemoteStorage remoteStorage)
        {

            throw new NotImplementedException();
        }

        /// <summary>
        /// It is guaranteed that OnStorageLeave will only be called
        /// on those storages that has been previously sent to
        /// [OnStorageJoin]
        /// </summary>
        /// <param name="remoteStorage"></param>
        public void OnStorageLeave(RemoteStorage remoteStorage)
        {

        }

        public override IEnumerable<int> MyChunkIds
        {
            get
            {
                List<int> my_hosting_chunks = new List<int>();
                my_hosting_chunks = server_host_chunk[m_partitionId];

                return my_hosting_chunks;
            }
        }

        public override int MyPartitionId
        {
            get
            {
                int my_partition_id = -1;
                my_partition_id = server_in_partition[m_partitionId];

                return my_partition_id;
            }
        }

        public override int MyProxyId
        {
            get
            {
                return my_proxy_id;
            }
        }

        public override int ProxyCount
        {
            get
            {
                return cluster_config.Proxies.Count;
            }
        }

        public override int PartitionCount
        {
            get
            {
                return DynamicClusterConfig.Instance.PartitionCount;
            }
        }

        public int ChunkCount
        {
            get
            {
                int chunk_count = -1;
                chunk_count = chunk_range.Count;

                return chunk_count;
            }
        }

        public override bool IsLocalCell(long cellId)
        {
            return (GetStorageByCellId(cellId) as ChunkedStorage).IsLocal(cellId);
        }

        public ChunkedStorage ChunkedStorageTable(int id) => StorageTable[id] as ChunkedStorage;

        public override bool Open(ClusterConfig config, bool nonblocking)
        {
            Log.WriteLine($"DynamicMemoryCloud: server {m_namedescriptor.Nickname} starting.");
            TrinityErrorCode errno = TrinityErrorCode.E_SUCCESS;

            this.cluster_config = config;
            my_partition_id = DynamicClusterConfig.Instance.LocalPartitionId;
            my_proxy_id = -1;
            partition_count = DynamicClusterConfig.Instance.PartitionCount;
            StorageTable = new Storage[partition_count];

            if (partition_count == 0)
                goto server_not_found;

            for (int i = 0; i < partition_count; i++)
            {
                StorageTable[i] = new ChunkedStorage();
            }

            if (TrinityErrorCode.E_SUCCESS != (errno = ChunkedStorageTable(my_partition_id).Mount(Global.LocalStorage)))
            {
                //TODO
            }
            
            m_nameservice = AssemblyUtility.GetAllClassInstances(t => t.GetConstructor(new Type[] { }).Invoke(new object[] { }) as INameService).First();
            m_nameservice.NewServerInfoPublished += (o, e) =>
            {
                var name = e.Item1;
                var si = e.Item2;
                Log.WriteLine($"DynamicCluster: New server info published: {name.Nickname}, {si.ToString()}");
                Task t = new Task(() => Connect(name, si));
                t.Start();
            };
            ServerInfo my_si = new ServerInfo(Global.MyIPAddress.ToString(), Global.MyIPEndPoint.Port, Global.MyAssemblyPath, TrinityConfig.LoggingLevel);
            m_nameservice.PublishServerInfo(m_namedescriptor, my_si);
            ServerConnected += DynamicMemoryCloud_ServerConnected;
            ServerDisconnected += DynamicMemoryCloud_ServerDisconnected;

            if (cluster_config.RunningMode == RunningMode.Server && !server_found)
            {
                goto server_not_found;
            }

            if (!nonblocking) { CheckServerProtocolSignatures(); } // TODO should also check in nonblocking setup when any remote storage is connected
            // else this.ServerConnected += (_, __) => {};

            return true;

            server_not_found:
            if (cluster_config.RunningMode == RunningMode.Server || cluster_config.RunningMode == RunningMode.Client)
                Log.WriteLine(LogLevel.Warning, "Incorrect server configuration. Message passing via CloudStorage not possible.");
            else if (cluster_config.RunningMode == RunningMode.Proxy)
                Log.WriteLine(LogLevel.Warning, "No servers are found. Message passing to servers via CloudStorage not possible.");
            return false;

        }

        public void Shutdown()
        {
            //TODO inform my peers that I'm leaving
        }

        private void DynamicMemoryCloud_ServerDisconnected(object sender, ServerStatusEventArgs e)
        {
            throw new NotImplementedException();
        }

        private void DynamicMemoryCloud_ServerConnected(object sender, ServerStatusEventArgs e)
        {
            throw new NotImplementedException();
        }

        private void Connect(NameDescriptor name, ServerInfo si)
        {
            Log.WriteLine($"DynamicCluster: connecting to {name} at {si.HostName}:{si.Port}");
            RemoteStorage rs = new RemoteStorage();
            OnStorageJoin(rs);
        }

        private void CheckServerProtocolSignatures()
        {
            Log.WriteLine("Checking {0}-Server protocol signatures...", cluster_config.RunningMode);
            int my_server_id = (cluster_config.RunningMode == RunningMode.Server) ? MyPartitionId : -1;
            var storage = StorageTable.Where((_, idx) => idx != my_server_id).FirstOrDefault() as RemoteStorage;

            CheckProtocolSignatures_impl(storage, cluster_config.RunningMode, RunningMode.Server);
        }

        private void CheckProxySignatures(IEnumerable<RemoteStorage> proxy_list)
        {
            Log.WriteLine("Checking {0}-Proxy protocol signatures...", cluster_config.RunningMode);
            int my_proxy_id = (cluster_config.RunningMode == RunningMode.Proxy) ? MyProxyId : -1;
            var storage = proxy_list.Where((_, idx) => idx != my_proxy_id).FirstOrDefault();

            CheckProtocolSignatures_impl(storage, cluster_config.RunningMode, RunningMode.Proxy);
        }

        internal int GetServerIdByIPE(IPEndPoint ipe)
        {
            for (int i = 0; i < cluster_config.Servers.Count; i++)
            {
                if (cluster_config.Servers[i].Has(ipe))
                    return i;
            }
            return -1;
        }
    }
}
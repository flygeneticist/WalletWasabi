using NBitcoin;
using NBitcoin.RPC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Backend.Models;
using WalletWasabi.Backend.Models.Responses;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Tor.Http.Bases;
using WalletWasabi.Tor.Http.Extensions;
using WalletWasabi.Tor.Http.Interfaces;

namespace WalletWasabi.WebClients.Wasabi
{
	public class WasabiClient : TorDisposableBase
	{
		/// <inheritdoc/>
		public WasabiClient(Func<Uri> baseUriAction, EndPoint torSocks5EndPoint) : base(baseUriAction, torSocks5EndPoint)
		{
		}

		/// <inheritdoc/>
		public WasabiClient(Uri baseUri, EndPoint torSocks5EndPoint) : base(baseUri, torSocks5EndPoint)
		{
		}

		public WasabiClient(ITorHttpClient torHttpClient) : base(torHttpClient)
		{
		}

		public static Dictionary<uint256, Transaction> TransactionCache { get; } = new Dictionary<uint256, Transaction>();
		private static Queue<uint256> TransactionIdQueue { get; } = new Queue<uint256>();
		public static object TransactionCacheLock { get; } = new object();
		public static ushort ApiVersion { get; private set; } = ushort.Parse(Helpers.Constants.BackendMajorVersion);

		#region batch

		/// <remarks>
		/// Throws OperationCancelledException if <paramref name="cancel"/> is set.
		/// </remarks>
		public async Task<SynchronizeResponse> GetSynchronizeAsync(uint256 bestKnownBlockHash, int count, EstimateSmartFeeMode? estimateMode = null, CancellationToken cancel = default)
		{
			string relativeUri = $"/api/v{ApiVersion}/btc/batch/synchronize?bestKnownBlockHash={bestKnownBlockHash}&maxNumberOfFilters={count}";
			if (estimateMode is { })
			{
				relativeUri = $"{relativeUri}&estimateSmartFeeMode={estimateMode}";
			}

			using var response = await TorClient.SendAndRetryAsync(HttpMethod.Get, relativeUri, token: cancel).ConfigureAwait(false);

			if (response.StatusCode != HttpStatusCode.OK)
			{
				await response.ThrowRequestExceptionFromContentAsync().ConfigureAwait(false);
			}

			using HttpContent content = response.Content;
			var ret = await content.ReadAsJsonAsync<SynchronizeResponse>().ConfigureAwait(false);
			return ret;
		}

		#endregion batch

		#region blockchain

		/// <remarks>
		/// Throws OperationCancelledException if <paramref name="cancel"/> is set.
		/// </remarks>
		public async Task<FiltersResponse> GetFiltersAsync(uint256 bestKnownBlockHash, int count, CancellationToken cancel = default)
		{
			using var response = await TorClient.SendAndRetryAsync(
				HttpMethod.Get,
				$"/api/v{ApiVersion}/btc/blockchain/filters?bestKnownBlockHash={bestKnownBlockHash}&count={count}",
				token: cancel).ConfigureAwait(false);

			if (response.StatusCode == HttpStatusCode.NoContent)
			{
				return null;
			}
			if (response.StatusCode != HttpStatusCode.OK)
			{
				await response.ThrowRequestExceptionFromContentAsync().ConfigureAwait(false);
			}

			using HttpContent content = response.Content;
			var ret = await content.ReadAsJsonAsync<FiltersResponse>().ConfigureAwait(false);
			return ret;
		}

		public async Task<IEnumerable<Transaction>> GetTransactionsAsync(Network network, IEnumerable<uint256> txHashes, CancellationToken cancel)
		{
			var allTxs = new List<Transaction>();
			var txHashesToQuery = new List<uint256>();
			lock (TransactionCacheLock)
			{
				var cachedTxs = TransactionCache.Where(x => txHashes.Contains(x.Key));
				allTxs.AddRange(cachedTxs.Select(x => x.Value));
				txHashesToQuery.AddRange(txHashes.Except(cachedTxs.Select(x => x.Key)));
			}

			foreach (IEnumerable<uint256> chunk in txHashesToQuery.ChunkBy(10))
			{
				cancel.ThrowIfCancellationRequested();

				using var response = await TorClient.SendAndRetryAsync(
					HttpMethod.Get,
					$"/api/v{ApiVersion}/btc/blockchain/transaction-hexes?&transactionIds={string.Join("&transactionIds=", chunk.Select(x => x.ToString()))}",
					token: cancel).ConfigureAwait(false);

				if (response.StatusCode != HttpStatusCode.OK)
				{
					await response.ThrowRequestExceptionFromContentAsync().ConfigureAwait(false);
				}

				using HttpContent content = response.Content;
				var retString = await content.ReadAsJsonAsync<IEnumerable<string>>().ConfigureAwait(false);
				var ret = retString.Select(x => Transaction.Parse(x, network)).ToList();

				lock (TransactionCacheLock)
				{
					foreach (var tx in ret)
					{
						tx.PrecomputeHash(false, true);
						if (TransactionCache.TryAdd(tx.GetHash(), tx))
						{
							TransactionIdQueue.Enqueue(tx.GetHash());
							if (TransactionCache.Count > 1000) // No more than 1000 txs in cache
							{
								var toRemove = TransactionIdQueue.Dequeue();
								TransactionCache.Remove(toRemove);
							}
						}
					}
				}
				allTxs.AddRange(ret);
			}

			return allTxs.ToDependencyGraph().OrderByDependency();
		}

		public async Task BroadcastAsync(string hex)
		{
			using var content = new StringContent($"'{hex}'", Encoding.UTF8, "application/json");
			using var response = await TorClient.SendAsync(HttpMethod.Post, $"/api/v{ApiVersion}/btc/blockchain/broadcast", content).ConfigureAwait(false);
			if (response.StatusCode != HttpStatusCode.OK)
			{
				await response.ThrowRequestExceptionFromContentAsync().ConfigureAwait(false);
			}
		}

		public async Task BroadcastAsync(Transaction transaction)
		{
			await BroadcastAsync(transaction.ToHex()).ConfigureAwait(false);
		}

		public async Task BroadcastAsync(SmartTransaction transaction)
		{
			await BroadcastAsync(transaction.Transaction).ConfigureAwait(false);
		}

		public async Task<IEnumerable<uint256>> GetMempoolHashesAsync(CancellationToken cancel = default)
		{
			using var response = await TorClient.SendAndRetryAsync(
				HttpMethod.Get,
				$"/api/v{ApiVersion}/btc/blockchain/mempool-hashes",
				token: cancel).ConfigureAwait(false);

			if (response.StatusCode != HttpStatusCode.OK)
			{
				await response.ThrowRequestExceptionFromContentAsync().ConfigureAwait(false);
			}

			using HttpContent content = response.Content;
			var strings = await content.ReadAsJsonAsync<IEnumerable<string>>().ConfigureAwait(false);
			var ret = strings.Select(x => new uint256(x));
			return ret;
		}

		/// <summary>
		/// Gets mempool hashes, but strips the last x characters of each hash.
		/// </summary>
		/// <param name="compactness">1 to 64</param>
		public async Task<ISet<string>> GetMempoolHashesAsync(int compactness, CancellationToken cancel = default)
		{
			using var response = await TorClient.SendAndRetryAsync(
				HttpMethod.Get,
				$"/api/v{ApiVersion}/btc/blockchain/mempool-hashes?compactness={compactness}",
				token: cancel).ConfigureAwait(false);

			if (response.StatusCode != HttpStatusCode.OK)
			{
				await response.ThrowRequestExceptionFromContentAsync().ConfigureAwait(false);
			}

			using HttpContent content = response.Content;
			var strings = await content.ReadAsJsonAsync<ISet<string>>().ConfigureAwait(false);
			return strings;
		}

		#endregion blockchain

		#region offchain

		public async Task<IEnumerable<ExchangeRate>> GetExchangeRatesAsync()
		{
			using var response = await TorClient.SendAndRetryAsync(HttpMethod.Get, $"/api/v{ApiVersion}/btc/offchain/exchange-rates").ConfigureAwait(false);

			if (response.StatusCode != HttpStatusCode.OK)
			{
				await response.ThrowRequestExceptionFromContentAsync().ConfigureAwait(false);
			}

			using HttpContent content = response.Content;
			var ret = await content.ReadAsJsonAsync<IEnumerable<ExchangeRate>>().ConfigureAwait(false);
			return ret;
		}

		#endregion offchain

		#region software

		public async Task<(Version ClientVersion, ushort BackendMajorVersion, Version LegalDocumentsVersion)> GetVersionsAsync(CancellationToken cancel)
		{
			using var response = await TorClient.SendAndRetryAsync(HttpMethod.Get, "/api/software/versions", token: cancel).ConfigureAwait(false);

			if (response.StatusCode != HttpStatusCode.OK)
			{
				await response.ThrowRequestExceptionFromContentAsync().ConfigureAwait(false);
			}

			using HttpContent content = response.Content;
			var resp = await content.ReadAsJsonAsync<VersionsResponse>().ConfigureAwait(false);

			return (Version.Parse(resp.ClientVersion), ushort.Parse(resp.BackendMajorVersion), Version.Parse(resp.LegalDocumentsVersion));
		}

		public async Task<UpdateStatus> CheckUpdatesAsync(CancellationToken cancel)
		{
			var versions = await GetVersionsAsync(cancel).ConfigureAwait(false);
			var clientUpToDate = Helpers.Constants.ClientVersion >= versions.ClientVersion; // If the client version locally is greater than or equal to the backend's reported client version, then good.
			var backendCompatible = int.Parse(Helpers.Constants.ClientSupportBackendVersionMax) >= versions.BackendMajorVersion && versions.BackendMajorVersion >= int.Parse(Helpers.Constants.ClientSupportBackendVersionMin); // If ClientSupportBackendVersionMin <= backend major <= ClientSupportBackendVersionMax, then our software is compatible.
			var currentBackendMajorVersion = versions.BackendMajorVersion;

			if (backendCompatible)
			{
				// Only refresh if compatible.
				ApiVersion = currentBackendMajorVersion;
			}

			return new UpdateStatus(backendCompatible, clientUpToDate, versions.LegalDocumentsVersion, currentBackendMajorVersion);
		}

		#endregion software

		#region wasabi

		public async Task<string> GetLegalDocumentsAsync(CancellationToken cancel)
		{
			using var response = await TorClient.SendAndRetryAsync(
				HttpMethod.Get,
				$"/api/v{ApiVersion}/wasabi/legaldocuments",
				token: cancel).ConfigureAwait(false);

			if (response.StatusCode != HttpStatusCode.OK)
			{
				await response.ThrowRequestExceptionFromContentAsync().ConfigureAwait(false);
			}

			using HttpContent content = response.Content;
			var ret = await content.ReadAsStringAsync().ConfigureAwait(false);
			return ret;
		}

		#endregion wasabi
	}
}

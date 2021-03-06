using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using WalletWasabi.CoinJoin.Common.Models;
using WalletWasabi.Helpers;
using WalletWasabi.Tor.Http.Bases;
using WalletWasabi.Tor.Http.Extensions;
using WalletWasabi.WebClients.Wasabi;

namespace WalletWasabi.CoinJoin.Client.Clients
{
	public class BobClient : TorDisposableBase
	{
		/// <inheritdoc/>
		public BobClient(Func<Uri> baseUriAction, EndPoint torSocks5EndPoint) : base(baseUriAction, torSocks5EndPoint)
		{
		}

		/// <inheritdoc/>
		public BobClient(Uri baseUri, EndPoint torSocks5EndPoint) : base(baseUri, torSocks5EndPoint)
		{
		}

		/// <returns>If the phase is still in OutputRegistration.</returns>
		public async Task<bool> PostOutputAsync(long roundId, ActiveOutput activeOutput)
		{
			Guard.MinimumAndNotNull(nameof(roundId), roundId, 0);
			Guard.NotNull(nameof(activeOutput), activeOutput);

			var request = new OutputRequest { OutputAddress = activeOutput.Address, UnblindedSignature = activeOutput.Signature, Level = activeOutput.MixingLevel };
			using var response = await TorClient.SendAsync(HttpMethod.Post, $"/api/v{WasabiClient.ApiVersion}/btc/chaumiancoinjoin/output?roundId={roundId}", request.ToHttpStringContent()).ConfigureAwait(false);
			if (response.StatusCode == HttpStatusCode.Conflict)
			{
				return false;
			}
			else if (response.StatusCode != HttpStatusCode.NoContent)
			{
				await response.ThrowRequestExceptionFromContentAsync().ConfigureAwait(false);
			}

			return true;
		}
	}
}

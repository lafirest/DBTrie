using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace DBTrie.Tests
{
	public class UnitTest1
	{
		private readonly ITestOutputHelper logs;

		public UnitTest1(ITestOutputHelper logs)
		{
			this.logs = logs;
		}

		[Fact]
		public async Task CacheTests()
		{
			CreateEmptyFile(1030);
			await using var fs = new FileStorage("Empty");
			var cache = new CacheStorage(fs, pageSize: 128);
			await fs.Write(125, "abcdefgh");
			Assert.Equal("abcdefgh", await cache.Read(125, "abcdefgh".Length));
			Assert.Equal("abcdefgh", await fs.Read(125, "abcdefgh".Length));
			await cache.Write(127, "CDEF");
			Assert.Equal("abCDEFgh", await cache.Read(125, "abCDEFgh".Length));
			Assert.Equal("abcdefgh", await fs.Read(125, "abcdefgh".Length));

			Assert.Equal(2, cache.pages.Count);
			Assert.Equal(1030, fs.Length);
			Assert.Equal(1030, cache.Length);
			await cache.WriteToEnd("helloworld");
			Assert.Equal(1030 + "helloworld".Length, cache.Length);
			Assert.Equal("helloworld", await cache.Read(1030, "helloworld".Length));
			await cache.WriteToEnd("abdwuqiwiw");

			Assert.NotEqual("helloworld", await fs.Read(1030, "helloworld".Length));

			Assert.Equal("abcdefgh", await fs.Read(125, "abcdefgh".Length));
			await cache.Flush();
			Assert.Equal("helloworld", await fs.Read(1030, "helloworld".Length));
			Assert.Equal("abCDEFgh", await fs.Read(125, "abCDEFgh".Length));

			Assert.Equal(cache.Length, fs.Length);
			Assert.Equal(1030 + "helloworldabdwuqiwiw".Length, fs.Length);
			await fs.Reserve(10);
			Assert.Equal(1030 + "helloworldabdwuqiwiw".Length + 10, fs.Length);
		}

		private static void CreateEmptyFile(int size)
		{
			if (File.Exists("Empty"))
				File.Create("Empty").Close();
			var file = File.Create("Empty");
			file.Seek(size - 1, SeekOrigin.Begin);
			file.WriteByte(0);
			file.Dispose();
		}

		[Fact]
		public async Task GeneralTests()
		{
			foreach (bool allowTrieCache in new[] { false, true })
			foreach (bool cacheStorageLayer in new[] { true, false })
			{
				await using var fs = CreateFileStorage("_DBreezeSchema", cacheStorageLayer);
				var trie = CreateTrie(fs, allowTrieCache);
				var rootNode = await trie.ReadRootNode();
				var generationNode = await rootNode.ReadGenerationNode();
				var result = await rootNode.GetKey("@@@@LastFileNumber");
				Assert.NotNull(result);
				Assert.Equal(64, result!.Pointer);
				Assert.Equal(89, result.ValuePointer);
				Assert.Equal(8, result.ValueLength);
				Assert.Null(await rootNode.GetKey("notexists"));
				Assert.Null(await rootNode.GetKey("@u"));
				Assert.Equal(4282, rootNode.RecordCount);

				var schema = new Schema(trie);
				Assert.True(await schema.TableExists("IndexProgress"));
				Assert.False(await schema.TableExists("In"));
				Assert.False(await schema.TableExists("IndexProgresss"));
				Assert.False(await schema.TableExists("IndexProgres"));

				var filename = await schema.GetFileNameOrCreate("IndexProgress");
				Assert.Equal(10000006UL, filename);
				Assert.Equal(10004281UL, await schema.GetLastFileNumber());

				// This should create a new record
				filename = await schema.GetFileNameOrCreate("NotExists");
				Assert.Equal(10004282UL, filename);
				Assert.Equal(10004282UL, await schema.GetLastFileNumber());
				Assert.Equal(4283, rootNode.RecordCount);

				// This should NOT create a new record
				filename = await schema.GetFileNameOrCreate("NotExists");
				Assert.Equal(10004282UL, filename);
				Assert.Equal(10004282UL, await schema.GetLastFileNumber());
				Assert.Equal(4283, rootNode.RecordCount);

				// Reloading the tree
				trie = ReloadTrie(trie);
				rootNode = await trie.ReadRootNode();

				// We should get back our created table
				filename = await schema.GetFileNameOrCreate("NotExists");
				Assert.Equal(10004282UL, filename);
				Assert.Equal(10004282UL, await schema.GetLastFileNumber());
				Assert.Equal(4283, rootNode.RecordCount);

				// Can list tables by name?
				schema = new Schema(trie);
				var tables = await schema.GetTables("TestTa").ToArrayAsync();
				var ordered = tables.OrderBy(o => o).ToArray();
				Assert.True(tables.SequenceEqual(ordered));
				Assert.Equal(4, tables.Length);
				tables = await schema.GetTables("TestT").ToArrayAsync();
				ordered = tables.OrderBy(o => o).ToArray();
				Assert.True(tables.SequenceEqual(ordered));
				Assert.Equal(4, tables.Length);
				tables = await schema.GetTables("TestTab").ToArrayAsync();
				ordered = tables.OrderBy(o => o).ToArray();
				Assert.True(tables.SequenceEqual(ordered));
				Assert.Equal(3, tables.Length);
				Assert.NotNull(await rootNode.GetRow("@utTestTa"));

				await AssertMatch(rootNode, false, "POFwoinfOWu");
				await AssertMatch(rootNode, false, "@utTestT");
				await AssertMatch(rootNode, true, "@utTestTa");
				await AssertMatch(rootNode, true, "@utIndexProg");
				await AssertMatch(rootNode, true, "@utIndexProgT");
				await AssertMatch(rootNode, true, "@utIndexProgressss");
				await AssertMatch(rootNode, true, "@utIndexProgresa");

				tables = await schema.GetTables().ToArrayAsync();
				Assert.Equal(4282, tables.Length);
				ordered = tables.OrderBy(o => o).ToArray();
				Assert.True(tables.SequenceEqual(ordered));
				var r = new Random(0);
				for (int i = 0; i == 10; i++)
				{
					var keys = new string[3];
					keys[0] = RandomWord(5, r);
					keys[1] = keys[0] + RandomWord(1, r);
					keys[2] = keys[1] + RandomWord(1, r);
					var fromShortest = keys.ToArray();
					r.Shuffle(keys);

					// Try adding tables with intermediates
					var recordCountBefore = rootNode.RecordCount;
					foreach (var k in keys)
						await schema.GetFileNameOrCreate(k);
					tables = await schema.GetTables(fromShortest[0]).ToArrayAsync();
					ordered = tables.OrderBy(o => o).ToArray();
					Assert.True(tables.SequenceEqual(ordered));
					Assert.Equal(keys.Length, tables.Length);
					Assert.Equal(recordCountBefore + keys.Length, rootNode.RecordCount);
					tables = await schema.GetTables(fromShortest[1]).ToArrayAsync();
					Assert.Equal(keys.Length - 1, tables.Length);

					// Reloading
					trie = ReloadTrie(trie);
					rootNode = await trie.ReadRootNode();

					// Make sure our tables are still here
					foreach (var k in keys)
						Assert.True(await schema.TableExists(k));
					tables = await schema.GetTables(fromShortest[0]).ToArrayAsync();
					Assert.Equal(keys.Length, tables.Length);
					Assert.Equal(recordCountBefore + keys.Length, rootNode.RecordCount);
				}
			}
		}

		[Fact]
		public async Task CanListTransactions()
		{
			foreach (bool allowTrieCache in new[] {  true })
			foreach (bool cacheStorageLayer in new[] { true })
			{
				logs.WriteLine($"allowTrieCache: {allowTrieCache}");
				logs.WriteLine($"cacheStorageLayer: {cacheStorageLayer}");
				await using var fs = CreateFileStorage("10000007", cacheStorageLayer);
				LTrie trie = CreateTrie(fs, allowTrieCache);
				trie.ConsistencyCheck = false;
				var rootNode = await trie.ReadRootNode();
				DateTimeOffset now = DateTimeOffset.UtcNow;
				int records = 0;
				await foreach(var row in rootNode.EnumerateStartWith(""))
				{
					records++;
				}
				logs.WriteLine($"Record count : {records}");
				logs.WriteLine($"Enumerate 1 time : {(int)(DateTimeOffset.UtcNow - now).TotalMilliseconds} ms");
				now = DateTimeOffset.UtcNow;
				await foreach (var row in rootNode.EnumerateStartWith(""))
				{
					
				}
				logs.WriteLine($"Enumerate 2 time : {(int)(DateTimeOffset.UtcNow - now).TotalMilliseconds} ms");
				now = DateTimeOffset.UtcNow;
				await foreach (var row in rootNode.EnumerateStartWith(""))
				{
					using var owner = trie.MemoryPool.Rent(row.ValueLength);
					await trie.Storage.Read(row.ValuePointer, owner.Memory.Slice(row.ValueLength));
				}
				logs.WriteLine($"Enumerate values : {(int)(DateTimeOffset.UtcNow - now).TotalMilliseconds} ms");
			}
		}

		[Fact]
		public async Task CanSetKeyValue()
		{
			foreach (bool allowTrieCache in new[] { false, true })
			foreach (bool cacheStorageLayer in new[] { true, false })
			{
				await using var fs = CreateFileStorage("_DBreezeSchema", cacheStorageLayer);
				LTrie trie = CreateTrie(fs, allowTrieCache);
				var rn = await trie.ReadRootNode();
				var countBefore = rn.RecordCount;
				Assert.Null(await rn.GetKey("CanSetKeyValue"));
				await rn.SetKey("CanSetKeyValue", "CanSetKeyValue-r1");
				Assert.Equal("CanSetKeyValue-r1", await rn.GetValue("CanSetKeyValue"));
				Assert.Equal(countBefore + 1, rn.RecordCount);
				await rn.SetKey("CanSetKeyValue", "CanSetKeyValue-r2");
				Assert.Equal("CanSetKeyValue-r2", await rn.GetValue("CanSetKeyValue"));
				Assert.Equal(countBefore + 1, rn.RecordCount);
				trie = ReloadTrie(trie);
				rn = await trie.ReadRootNode();
				Assert.Equal(countBefore + 1, rn.RecordCount);
				Assert.Equal("CanSetKeyValue-r2", await rn.GetValue("CanSetKeyValue"));

				Assert.Null(await rn.GetKey("Relocation"));
				await rn.SetKey("Relocation", "a");
				Assert.Equal("a", await rn.GetValue("Relocation"));
				Assert.Equal(countBefore + 2, rn.RecordCount);

				Assert.Null(await rn.GetKey("NoRelocation"));
				await rn.SetKey("NoRelocation", "b");
				Assert.Equal("b", await rn.GetValue("NoRelocation"));
				Assert.Equal(countBefore + 3, rn.RecordCount);

				trie = ReloadTrie(trie);
				rn = await trie.ReadRootNode();
				Assert.Equal("a", await rn.GetValue("Relocation"));
				Assert.Equal("b", await rn.GetValue("NoRelocation"));
				Assert.Equal("CanSetKeyValue-r2", await rn.GetValue("CanSetKeyValue"));
				Assert.Equal(countBefore + 3, rn.RecordCount);

				Assert.Null(await rn.GetKey("k"));
				await rn.SetKey("k", "k-r1");
				Assert.Equal("k-r1", await rn.GetValue("k"));
				await rn.SetKey("k", "k-r2");
				Assert.Equal("k-r2", await rn.GetValue("k"));
				Assert.Equal(countBefore + 4, rn.RecordCount);

				Assert.Null(await rn.GetKey("CanSetKeyValue-Extended"));
				await rn.SetKey("CanSetKeyValue-Extended", "CanSetKeyValue-Extended-r1");
				Assert.Equal("CanSetKeyValue-Extended-r1", await rn.GetValue("CanSetKeyValue-Extended"));
				await rn.SetKey("CanSetKeyValue-Extended", "CanSetKeyValue-Extended-r2");
				Assert.Equal(countBefore + 5, rn.RecordCount);

				Assert.Equal("CanSetKeyValue-Extended-r2", await rn.GetValue("CanSetKeyValue-Extended"));
				Assert.Equal("CanSetKeyValue-r2", await rn.GetValue("CanSetKeyValue"));
				Assert.Equal("k-r2", await rn.GetValue("k"));
				Assert.Equal("a", await rn.GetValue("Relocation"));
				Assert.Equal("b", await rn.GetValue("NoRelocation"));

				trie = ReloadTrie(trie);
				rn = await trie.ReadRootNode();

				Assert.Equal("CanSetKeyValue-Extended-r2", await rn.GetValue("CanSetKeyValue-Extended"));
				Assert.Equal("CanSetKeyValue-r2", await rn.GetValue("CanSetKeyValue"));
				Assert.Equal("k-r2", await rn.GetValue("k"));
				Assert.Equal("a", await rn.GetValue("Relocation"));
				Assert.Equal("b", await rn.GetValue("NoRelocation"));
				Assert.Equal(countBefore + 5, rn.RecordCount);

				List<string> insertedKeys = new List<string>();
				Random r = new Random(0);
				for (int i = 0; i < 100; i++)
				{
					countBefore = rn.RecordCount;
					var keys = new string[5];
					int o = 0;
					var startWith = r.PickRandom(new[] {
						"@ut",
						"@",
						"k",
						"CanSetKeyValue",
						"CanSetKeyValueee",
						"CanSetKeyValue-Extended",
						"Relo",
						"Relocationn",
						"R",
						"NoRelocation",
						"" });
					keys[o++] = startWith + RandomWord(5, r);
					keys[o++] = keys[o - 2] + RandomWord(1, r);
					keys[o++] = keys[o - 2] + RandomWord(1, r);
					keys[o++] = keys[o - 2] + RandomWord(1, r);
					keys[o++] = keys[o - 2] + RandomWord(1, r);
					var fromShortest = keys.ToArray();
					r.Shuffle(keys);

					foreach (var k in keys)
					{
						Assert.Equal("CanSetKeyValue-Extended-r2", await rn.GetValue("CanSetKeyValue-Extended"));
						if (i == 42)
						{
						}
						await rn.SetKey(k, k);
						Assert.Equal("CanSetKeyValue-Extended-r2", await rn.GetValue("CanSetKeyValue-Extended"));
						Assert.Equal(k, await rn.GetValue(k));
						insertedKeys.Add(k);
					}
					foreach (var k in keys)
					{
						Assert.Equal(k, await rn.GetValue(k));
					}
					Assert.Equal(countBefore + keys.Length, rn.RecordCount);
				}
				countBefore = rn.RecordCount;
				// Everything kept value
				foreach (var k in insertedKeys)
				{
					Assert.Equal(k, await rn.GetValue(k));
				}
				// Randomly edit stuff
				HashSet<string> edited = new HashSet<string>();
				foreach (var k in insertedKeys)
				{
					if (r.Next() % 2 == 0)
					{
						await rn.SetKey(k, k + "-r2");
						edited.Add(k);
					}
				}
				// Everything kept value
				foreach (var k in insertedKeys)
				{
					var expected = edited.Contains(k) ? k + "-r2" : k;
					Assert.Equal(expected, await rn.GetValue(k));
				}

				// Randomly trucate
				HashSet<string> truncated = new HashSet<string>();
				foreach (var k in insertedKeys)
				{
					if (r.Next() % 2 == 0)
					{
						await rn.SetKey(k, k.GetHashCode().ToString());
						truncated.Add(k);
					}
				}

				// Everything kept value
				foreach (var k in insertedKeys)
				{
					var expected = 
						truncated.Contains(k) ? k.GetHashCode().ToString() :
						edited.Contains(k) ? k + "-r2" : k;
					Assert.Equal(expected, await rn.GetValue(k));
				}
				Assert.Equal(countBefore, rn.RecordCount);
				// Nothing else got edited
				Assert.Equal("CanSetKeyValue-Extended-r2", await rn.GetValue("CanSetKeyValue-Extended"));
				Assert.Equal("CanSetKeyValue-r2", await rn.GetValue("CanSetKeyValue"));
				Assert.Equal("k-r2", await rn.GetValue("k"));
				Assert.Equal("a", await rn.GetValue("Relocation"));
				Assert.Equal("b", await rn.GetValue("NoRelocation"));

				// Reload the trie
				trie = ReloadTrie(trie);
				rn = await trie.ReadRootNode();
				// Everything kept value
				foreach (var k in insertedKeys)
				{
					var expected =
						truncated.Contains(k) ? k.GetHashCode().ToString() :
						edited.Contains(k) ? k + "-r2" : k;
					Assert.Equal(expected, await rn.GetValue(k));
				}
				Assert.Equal(countBefore, rn.RecordCount);
				// Nothing else got edited
				Assert.Equal("CanSetKeyValue-Extended-r2", await rn.GetValue("CanSetKeyValue-Extended"));
				Assert.Equal("CanSetKeyValue-r2", await rn.GetValue("CanSetKeyValue"));
				Assert.Equal("k-r2", await rn.GetValue("k"));
				Assert.Equal("a", await rn.GetValue("Relocation"));
				Assert.Equal("b", await rn.GetValue("NoRelocation"));
			}
		}

		private LTrie ReloadTrie(LTrie trie)
		{
			var cache = trie.Storage as CacheStorage;
			var trie2 = CreateTrie(trie.Storage, trie.GenerationNodeCache is { });
			trie2.ConsistencyCheck = trie.ConsistencyCheck;
			return trie2;
		}

		private static LTrie CreateTrie(IStorage fs, bool allowGenerationNodeCache)
		{
			var trie = new LTrie(fs) { ConsistencyCheck = true };
			if (allowGenerationNodeCache)
				trie.ActivateCache();
			return trie;
		}

		private async Task AssertMatch(LTrieRootNode rootNode, bool linkToValue, string search)
		{
			var result = await rootNode.FindBestMatch(Encoding.UTF8.GetBytes(search));
			Assert.Equal(linkToValue, result.ValueKid is LTrieKid);
		}

		private string RandomWord(int minSize, Random r)
		{
			var alphabet = new[] { 'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j' };
			var count = r.Next(minSize, 10);
			return new string(Enumerable.Range(0, count)
				.Select(_ => r.PickRandom(alphabet))
				.ToArray());
		}

		private IStorage CreateFileStorage(string file, bool cacheStorageLayer, [CallerMemberName] string? caller = null)
		{
			if (caller is null)
				throw new ArgumentNullException(nameof(caller));
			Directory.CreateDirectory(caller);
			File.Copy($"Data/{file}", $"{caller}/{file}", true);
			var fs = new FileStorage($"{caller}/{file}");
			if (!cacheStorageLayer)
				return fs;
			return new CacheStorage(fs);
		}
	}
}
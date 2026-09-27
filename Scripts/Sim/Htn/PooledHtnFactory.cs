using System;
using System.Collections.Generic;
using FluidHTN.Factory;

namespace Gdpyr.Sim.Htn;

/// <summary>
/// FluidHTN's <see cref="IFactory"/> with free lists, so that planning allocates
/// nothing once the pools are warm (docs/HTN_BOTS.md §3.5, §4.2 rule 3). FluidHTN
/// hands every array, queue and list it borrows back through the matching
/// <c>Free*</c>, so a free list per element type — and per length, for arrays — is
/// all it takes. <see cref="DefaultFactory"/> allocates on every replan: 2.9 KB a
/// server tick at 64 agents, ~210 MB over a round.
///
/// What comes out is what <see cref="DefaultFactory"/> would have made: arrays are
/// zeroed and collections empty.
///
/// The pools belong to the instance and nothing locks them. The server tick is
/// single-threaded, and a domain and the contexts planned against it share one
/// factory on that thread (§3.4, P6); planning on another thread means another
/// domain and another factory.
/// </summary>
public sealed class PooledHtnFactory : IFactory
{
	private readonly Dictionary<Type, object> _pools = new();

	public T[] CreateArray<T>(int length) =>
		PoolOf<T>().Arrays.TryGetValue(length, out Stack<T[]> free) && free.Count > 0 ? free.Pop() : new T[length];

	public bool FreeArray<T>(ref T[] array)
	{
		if (array != null)
		{
			Dictionary<int, Stack<T[]>> arrays = PoolOf<T>().Arrays;
			if (!arrays.TryGetValue(array.Length, out Stack<T[]> free))
			{
				arrays[array.Length] = free = new Stack<T[]>();
			}

			Array.Clear(array);
			free.Push(array);
			array = null;
		}

		return true;
	}

	public Queue<T> CreateQueue<T>()
	{
		Stack<Queue<T>> free = PoolOf<T>().Queues;
		return free.Count > 0 ? free.Pop() : new Queue<T>();
	}

	public bool FreeQueue<T>(ref Queue<T> queue)
	{
		if (queue != null)
		{
			queue.Clear();
			PoolOf<T>().Queues.Push(queue);
			queue = null;
		}

		return true;
	}

	public List<T> CreateList<T>()
	{
		Stack<List<T>> free = PoolOf<T>().Lists;
		return free.Count > 0 ? free.Pop() : new List<T>();
	}

	public bool FreeList<T>(ref List<T> list)
	{
		if (list != null)
		{
			list.Clear();
			PoolOf<T>().Lists.Push(list);
			list = null;
		}

		return true;
	}

	/// <summary>Not pooled: FluidHTN at the pinned commit never calls it.</summary>
	public T Create<T>() where T : new() => new();

	public bool Free<T>(ref T obj)
	{
		obj = default;
		return true;
	}

	private Pool<T> PoolOf<T>()
	{
		if (!_pools.TryGetValue(typeof(T), out object pool))
		{
			pool = new Pool<T>();
			_pools.Add(typeof(T), pool);
		}

		return (Pool<T>)pool;
	}

	private sealed class Pool<T>
	{
		public readonly Dictionary<int, Stack<T[]>> Arrays = new();
		public readonly Stack<Queue<T>> Queues = new();
		public readonly Stack<List<T>> Lists = new();
	}
}

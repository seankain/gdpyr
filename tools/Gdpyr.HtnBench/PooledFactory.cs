using System.Collections.Generic;
using FluidHTN.Factory;

namespace Gdpyr.HtnBench;

/// <summary>
/// FluidHTN hands every array and queue it borrows back through <c>Free*</c>
/// (its README calls the factory "used internally with the support of pooling in
/// mind"), so a free list per element type and length is all it takes to plan
/// without allocating. Single-threaded, as the server tick is.
/// </summary>
public sealed class PooledFactory : IFactory
{
	private static class Arrays<T>
	{
		public static readonly Dictionary<int, Stack<T[]>> ByLength = new();
	}

	private static class Queues<T>
	{
		public static readonly Stack<Queue<T>> Free = new();
	}

	private static class Lists<T>
	{
		public static readonly Stack<List<T>> Free = new();
	}

	public T[] CreateArray<T>(int length) =>
		Arrays<T>.ByLength.TryGetValue(length, out Stack<T[]> free) && free.Count > 0 ? free.Pop() : new T[length];

	public bool FreeArray<T>(ref T[] array)
	{
		if (array != null)
		{
			if (!Arrays<T>.ByLength.TryGetValue(array.Length, out Stack<T[]> free))
			{
				Arrays<T>.ByLength[array.Length] = free = new Stack<T[]>();
			}

			free.Push(array);
			array = null;
		}

		return true;
	}

	public Queue<T> CreateQueue<T>() => Queues<T>.Free.Count > 0 ? Queues<T>.Free.Pop() : new Queue<T>();

	public bool FreeQueue<T>(ref Queue<T> queue)
	{
		if (queue != null)
		{
			queue.Clear();
			Queues<T>.Free.Push(queue);
			queue = null;
		}

		return true;
	}

	public List<T> CreateList<T>() => Lists<T>.Free.Count > 0 ? Lists<T>.Free.Pop() : new List<T>();

	public bool FreeList<T>(ref List<T> list)
	{
		if (list != null)
		{
			list.Clear();
			Lists<T>.Free.Push(list);
			list = null;
		}

		return true;
	}

	public T Create<T>() where T : new() => new();

	public bool Free<T>(ref T obj)
	{
		obj = default;
		return true;
	}
}

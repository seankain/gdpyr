using System.Collections.Generic;
using Gdpyr.Sim.Htn;
using Xunit;

namespace Gdpyr.Tests;

public class PooledHtnFactoryTests
{
	[Fact]
	public void AFreedArray_ComesBackForTheSameLength_Zeroed()
	{
		var factory = new PooledHtnFactory();
		int[] array = factory.CreateArray<int>(4);
		int[] freed = array;
		array[2] = 7;

		Assert.True(factory.FreeArray(ref array));
		Assert.Null(array);

		int[] again = factory.CreateArray<int>(4);
		Assert.Same(freed, again);
		Assert.Equal(new int[4], again);
	}

	[Fact]
	public void AFreedArray_IsNotHandedOutForAnotherLength()
	{
		var factory = new PooledHtnFactory();
		int[] array = factory.CreateArray<int>(4);
		int[] freed = array;
		factory.FreeArray(ref array);

		Assert.NotSame(freed, factory.CreateArray<int>(5));
	}

	[Fact]
	public void AFreedQueue_ComesBackEmpty()
	{
		var factory = new PooledHtnFactory();
		Queue<string> queue = factory.CreateQueue<string>();
		Queue<string> freed = queue;
		queue.Enqueue("stage");

		Assert.True(factory.FreeQueue(ref queue));
		Assert.Null(queue);

		Queue<string> again = factory.CreateQueue<string>();
		Assert.Same(freed, again);
		Assert.Empty(again);
	}

	[Fact]
	public void AFreedList_ComesBackEmpty()
	{
		var factory = new PooledHtnFactory();
		List<string> list = factory.CreateList<string>();
		List<string> freed = list;
		list.Add("strike");

		Assert.True(factory.FreeList(ref list));
		Assert.Null(list);

		List<string> again = factory.CreateList<string>();
		Assert.Same(freed, again);
		Assert.Empty(again);
	}

	[Fact]
	public void FreeingNothing_IsHarmless()
	{
		var factory = new PooledHtnFactory();
		int[] array = null;
		Queue<int> queue = null;
		List<int> list = null;

		Assert.True(factory.FreeArray(ref array));
		Assert.True(factory.FreeQueue(ref queue));
		Assert.True(factory.FreeList(ref list));
		Assert.NotNull(factory.CreateQueue<int>());
	}

	[Fact]
	public void TwoFactories_ShareNoPool()
	{
		// What makes a factory per thread safe: nothing one frees is handed out by another.
		var first = new PooledHtnFactory();
		var second = new PooledHtnFactory();
		Queue<int> queue = first.CreateQueue<int>();
		Queue<int> freed = queue;
		first.FreeQueue(ref queue);

		Assert.NotSame(freed, second.CreateQueue<int>());
		Assert.Same(freed, first.CreateQueue<int>());
	}
}

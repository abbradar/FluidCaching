﻿using System;
using System.Collections;
using System.Collections.Generic;

namespace FluidCaching
{
    internal class OrderedAgeBagCollection<T> : IEnumerable<AgeBag<T>> where T : class
    {
        private readonly AgeBag<T>[] bags;

        public OrderedAgeBagCollection(int capacity)
        {
            bags = new AgeBag<T>[capacity];

            for (int loop = capacity - 1; loop >= 0; --loop)
            {
                bags[loop] = new AgeBag<T>();
            }
        }

        public AgeBag<T> this[int number]
        {
            get
            {
                if (number == int.MaxValue)
                {
                    throw new OverflowException("The bag number has reached its max value");
                }

                if (number < 0)
                {
                    throw new ArgumentException("The bag number must be positive");
                }

                int index = number % bags.Length;
                return bags[index];
            }
        }

        public int Count => bags.Length;

        /// <summary>
        /// Empties the bags in the current set.
        /// </summary>
        /// <remarks>
        /// Emptying here means that all nodes in all bags are disassociated from the bag.
        /// </remarks>
        public void Empty()
        {
            foreach (AgeBag<T> bag in bags)
            {
                Node<T> node = bag.First;
                bag.First = null;
                while (node != null)
                {
                    Node<T> next = node.Next;
                    node.RemoveFromCache();
                    node = next;
                }
            }
        }

        public IEnumerator<AgeBag<T>> GetEnumerator()
        {
            return ((IEnumerable<AgeBag<T>>)bags).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
    }
}
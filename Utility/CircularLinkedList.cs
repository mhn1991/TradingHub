namespace Utility;

public class CircularLinkedList<T>
{
    private class Node
    {
        public T Data;
        public Node Next;

        public Node(T data)
        {
            Data = data;
            Next = null;
        }
    }

    private Node tail; // Points to the last node in the list
    private int count;

    public int Count => count;

    public bool IsEmpty => count == 0;

    // Add to the end of the list
    public void Add(T data)
    {
        Node newNode = new Node(data);

        if (tail == null) // Empty list
        {
            tail = newNode;
            tail.Next = tail; // Point to itself
        }
        else
        {
            newNode.Next = tail.Next; // New node points to the first node
            tail.Next = newNode; // Tail points to the new node
            tail = newNode; // Update tail to the new node
        }

        count++;
    }

    // Remove the first node
    public T Remove()
    {
        if (IsEmpty)
            throw new InvalidOperationException("The list is empty.");

        Node head = tail.Next; // First node
        if (tail == head) // Only one node
        {
            tail = null; // List becomes empty
        }
        else
        {
            tail.Next = head.Next; // Tail points to the second node
        }

        count--;
        return head.Data;
    }

    // Traverse the list
    public void Traverse(Action<T> action)
    {
        if (IsEmpty)
            return;

        Node current = tail.Next; // Start from the first node
        do
        {
            action(current.Data);
            current = current.Next;
        } while (current != tail.Next); // Stop when back at the first node
    }
}
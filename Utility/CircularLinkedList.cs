namespace Utility;

public class CircularLinkedList<T>
{
    public class Node
    {
        public T Data;
        public Node Next;
        public Node Previous;

        public Node(T data)
        {
            Data = data;
            Next = null;
            Previous = null;
        }
    }

    private Node head; // Points to the last node in the list
    private Node current;
    private int size;

    public int Size => size;

    public bool IsEmpty => size == 0;

    // Add to the end of the list
    public void Add(T data)
    {
        Node newNode = new Node(data);

        if (head == null) // Empty list
        {
            head = newNode;
            head.Next = head; // Point to itself
            head.Previous = head;
            current = head;
        }
        else
        {
            Node tail = head.Previous; // Get the last node (tail)

            // Update pointers to add the new node
            tail.Next = newNode; // Tail's next points to the new node
            newNode.Previous = tail; // New node's previous points to tail
            newNode.Next = head; // New node's next points to head
            head.Previous = newNode; // Head's previous points to the new node
        }

        size +=1 ;
    }

    public Node GetHead()
    {
        return head;
    }
    
    public Node GetCurrent()
    {
        return current;
    }
    
    public Node GetNext()
    {
        return current.Next;
    }

    public void MoveNext()
    {
        current = current.Next;
    }
}
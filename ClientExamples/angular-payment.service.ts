import { Injectable, NgZone } from '@angular/core';
import { Observable } from 'rxjs';

export interface PaymentRequestDto {
  amount: number;
  cardToken: string;
  customerEmail?: string;
}

export interface PaymentStatus {
  paymentId: string;
  status: 'Queued' | 'Processing' | 'SendingToProcessor' | 'Completed' | 'Failed';
  message: string;
  queuePosition?: number;
  timestamp: string;
}

@Injectable({
  providedIn: 'root'
})
export class PaymentService {
  private apiUrl = 'https://localhost:7000/api/payment'; // Update port as needed

  constructor(private zone: NgZone) {}

  /**
   * Process a payment with real-time status updates via Server-Sent Events.
   * Returns an Observable that emits status updates and completes when payment is done.
   */
  processPayment(request: PaymentRequestDto): Observable<PaymentStatus | { paymentId: string; message: string }> {
    return new Observable(observer => {
      // Make POST request
      fetch(`${this.apiUrl}/process`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(request)
      })
      .then(response => {
        if (!response.ok) {
          throw new Error(`HTTP error! status: ${response.status}`);
        }

        // Read SSE stream
        const reader = response.body!.getReader();
        const decoder = new TextDecoder();

        const readStream = (): void => {
          reader.read().then(({ done, value }) => {
            if (done) {
              this.zone.run(() => observer.complete());
              return;
            }

            // Decode and process chunk
            const chunk = decoder.decode(value);
            const lines = chunk.split('\n');

            for (let line of lines) {
              if (line.startsWith('data: ')) {
                const data = line.substring(6);
                try {
                  const status = JSON.parse(data);
                  this.zone.run(() => observer.next(status));

                  // Complete on terminal states
                  if (status.status === 'Completed' || status.status === 'Failed') {
                    reader.cancel();
                    this.zone.run(() => observer.complete());
                    return;
                  }
                } catch (e) {
                  console.error('Error parsing JSON:', e);
                }
              }
            }

            // Continue reading
            readStream();
          }).catch(error => {
            this.zone.run(() => observer.error(error));
          });
        };

        readStream();
      })
      .catch(error => {
        this.zone.run(() => observer.error(error));
      });

      // Cleanup function
      return () => {
        console.log('Payment observable unsubscribed');
      };
    });
  }

  /**
   * Get statistics about payment processing.
   */
  async getStatistics(): Promise<any> {
    const response = await fetch(`${this.apiUrl}/status/stats`);
    if (!response.ok) {
      throw new Error(`HTTP error! status: ${response.status}`);
    }
    return response.json();
  }
}

